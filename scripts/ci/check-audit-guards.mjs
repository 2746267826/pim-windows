#!/usr/bin/env node
/**
 * 审计整改（阶段 A）· REQ-WIN-02 / REQ-COM-02 / REQ-COM-04 / REQ-COM-05 / REQ-COM-07。
 *
 * 本文件是**检查设施**，不是修复实现：它把审计里那些“只能由 CI/工作流/仓库结构决定”
 * 的问题变成可重复执行、可故障对照的判定，供实现阶段接入必需 job。
 * 每条检查都对应 findings.json 里的一个 ID，违反项会带上该 ID 输出。
 *
 * 设计原则（避免“源码里 contains 某字符串”式的假检查）：
 *   - 工作流条件（F-05）用**真实求解**：把 release 的 if 表达式和 needs 列表取出来，
 *     逐 job 注入 failure/skipped/cancelled 结果后求值，判断“测试挂了还会不会发版”。
 *   - 触发语义（F-22）用**真实执行** scripts/ci/resolve-version.sh 并读它的 is_release 输出。
 *   - 其余结构性断言（pin/清单/文档/死代码）是仓库不变量，逐条给出可核对的证据。
 *
 * 用法：
 *   node scripts/ci/check-audit-guards.mjs [--root <dir>] [--check <id>] [--json]
 * 退出码：0 = 全部通过；1 = 存在违反项；2 = 用法/环境错误。
 */

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');

// ---------------------------------------------------------------------------
// 极简工作流读取：只解析这些文件里真实出现的结构，够用且不引入 YAML 依赖。
// ---------------------------------------------------------------------------

function readWorkflow(root, relPath) {
  const abs = path.join(root, relPath);
  if (!fs.existsSync(abs)) return null;
  return fs.readFileSync(abs, 'utf8');
}

function workflowFiles(root) {
  const dir = path.join(root, '.github', 'workflows');
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter((f) => f.endsWith('.yml') || f.endsWith('.yaml')).sort();
}

/** 解析 `jobs:` 下的每个 job：needs 列表 + if 表达式 + 整块正文。 */
function parseJobs(text) {
  const lines = text.split(/\r?\n/);
  const start = lines.findIndex((l) => /^jobs:\s*$/.test(l));
  if (start < 0) return {};

  const jobs = {};
  let current = null;
  for (let i = start + 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (/^\S/.test(line) && line.trim() !== '') break; // 回到顶层键，jobs 段结束

    const jobStart = /^ {2}([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (jobStart) {
      current = { name: jobStart[1], needs: [], if: '', body: [] };
      jobs[current.name] = current;
      continue;
    }
    if (!current) continue;
    current.body.push(line);
  }

  for (const job of Object.values(jobs)) {
    const body = job.body.join('\n');
    job.needs = parseNeeds(job.body);
    job.if = parseIf(job.body);
    job.bodyText = body;
  }
  return jobs;
}

function parseNeeds(bodyLines) {
  for (let i = 0; i < bodyLines.length; i += 1) {
    const inline = /^ {4}needs:\s*\[(.*)\]\s*$/.exec(bodyLines[i]);
    if (inline) {
      return inline[1].split(',').map((s) => s.trim().replace(/^['"]|['"]$/g, '')).filter(Boolean);
    }
    if (/^ {4}needs:\s*$/.test(bodyLines[i])) {
      const items = [];
      for (let j = i + 1; j < bodyLines.length; j += 1) {
        const item = /^ {6}- (.+?)\s*$/.exec(bodyLines[j]);
        if (!item) break;
        items.push(item[1].trim().replace(/^['"]|['"]$/g, ''));
      }
      return items;
    }
  }
  return [];
}

function parseIf(bodyLines) {
  for (let i = 0; i < bodyLines.length; i += 1) {
    const inline = /^ {4}if:\s*(.+?)\s*$/.exec(bodyLines[i]);
    if (inline && inline[1] !== '|' && inline[1] !== '>') return inline[1];
    if (/^ {4}if:\s*[|>]\s*$/.test(bodyLines[i])) {
      const block = [];
      for (let j = i + 1; j < bodyLines.length; j += 1) {
        if (/^\s*$/.test(bodyLines[j])) {
          block.push('');
          continue;
        }
        if (/^ {4}\S/.test(bodyLines[j])) break;
        block.push(bodyLines[j].trim());
      }
      return block.join(' ').trim();
    }
  }
  return '';
}

// ---------------------------------------------------------------------------
// GitHub Actions `if` 子集求解器：enough for the conditions this repo writes.
// ---------------------------------------------------------------------------

function evaluateClause(clause, needs, results, outputs) {
  // 先归一化外层括号：只剥掉“整体包裹”的那一层，不能把 `always()` 的括号吃掉。
  let c = clause.trim();
  while (c.startsWith('(') && c.endsWith(')') && isBalanced(c.slice(1, -1))) {
    c = c.slice(1, -1).trim();
  }
  if (c === 'always()') return true;
  if (c === '') return true;

  const contains = /^(!?)contains\(needs\.\*\.result,\s*'([^']+)'\)$/.exec(c);
  if (contains) {
    const negated = contains[1] === '!';
    const wanted = contains[2];
    // GitHub 语义关键点：needs.* 只覆盖**该 job 自己 needs 列表里**的 job。
    const hit = needs.some((n) => results[n] === wanted);
    return negated ? !hit : hit;
  }

  const eq = /^needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*==\s*'([^']*)'$/.exec(c);
  if (eq) {
    return (outputs[eq[1]] ?? {})[eq[2]] === eq[3];
  }

  // 认不出来的子句按“不确定”处理，宁可报错也不假装通过。
  throw new Error(`无法求解的 workflow 条件子句: ${c}`);
}

function isBalanced(text) {
  let depth = 0;
  for (const ch of text) {
    if (ch === '(') depth += 1;
    else if (ch === ')') {
      depth -= 1;
      if (depth < 0) return false;
    }
  }
  return depth === 0;
}

function evaluateIf(expr, needs, results, outputs) {
  if (!expr.trim()) return true;
  return expr
    .split('&&')
    .map((part) => part.trim())
    .filter((part) => part !== '')
    .every((part) => evaluateClause(part, needs, results, outputs));
}

// ---------------------------------------------------------------------------
// Checks. 每个 check: (root) => Array<{finding, message, evidence?}>
// ---------------------------------------------------------------------------

/**
 * F-05：发版必须被**完整的**必需 job 集合挡住。
 *
 * 判定不依赖“把 job 塞进 release.needs 就够了”，而是对每个必需 job 逐一注入
 * failure / skipped / cancelled（外加“没有结果”的 missing 情形）后真实求解 release 的
 * if 表达式。任何一次求解得到“会发版”都是违反。
 *
 * 关键：`needs.*.result` 只覆盖**该 job 自己 needs 列表里的** job。所以一个必需 job
 * 若不在 release.needs 里，注入它的 failure 对 release 的条件不可见 —— 这正是审计
 * 指出的“测试挂了还会发版”。只把 job 列进 needs、却没让条件检查该结果，同样会被这里的
 * 注入求解抓到。
 */
function checkReleaseGate(root) {
  const ci = readWorkflow(root, '.github/workflows/ci.yml');
  if (ci === null) return [{ finding: 'F-05', message: '找不到 .github/workflows/ci.yml' }];

  const jobs = parseJobs(ci);
  const release = jobs.release;
  const gate = jobs.gate;
  if (!release) return [{ finding: 'F-05', message: 'ci.yml 没有 release job' }];
  if (!gate) return [{ finding: 'F-05', message: 'ci.yml 没有 gate job' }];

  const violations = [];
  const required = [...gate.needs, ...gate.needs.map((n) => `${n}`), 'gate']
    .filter((v, i, arr) => arr.indexOf(v) === i);

  // ① 每个必需 job 都必须出现在 release.needs 里；否则它的结果对 release 不可见。
  const missingFromNeeds = required.filter((job) => !release.needs.includes(job));
  if (missingFromNeeds.length > 0) {
    violations.push({
      finding: 'F-05',
      message: `release 的 needs 没有覆盖必需 job：[${missingFromNeeds.join(', ')}]。`
        + '这些 job 的结果不会出现在 needs.*.result 里，失败/跳过/cancel 都不阻止发版。',
      evidence: `release.needs=[${release.needs.join(', ')}]`,
    });
  }

  // ② 对已在 needs 里的每个必需 job，逐一注入非 success 结果求解。
  const results = ['failure', 'skipped', 'cancelled'];
  const outputs = { 'resolve-version': { is_release: 'true' } };

  for (const job of release.needs) {
    for (const state of results) {
      const injected = {};
      for (const n of release.needs) injected[n] = 'success';
      injected[job] = state;

      let wouldRun;
      try {
        wouldRun = evaluateIf(release.if, release.needs, injected, outputs);
      } catch (error) {
        // 求解不了的表达式按 fail-closed 处理：报违反，而不是假装通过。
        violations.push({
          finding: 'F-05',
          message: `注入 ${job}=${state} 时无法求解 release 的 if 表达式（${error.message}）。`
            + '无法求解的条件不能被当作“不会发版”。',
          evidence: `release.if = ${release.if}`,
        });
        continue;
      }

      if (wouldRun) {
        violations.push({
          finding: 'F-05',
          message: `必需 job "${job}" 的结果是 "${state}" 时 release 仍会执行 —— `
            + '这是审计里“测试失败/被跳过仍会发版”的形态。',
          evidence: `release.if = ${release.if}`,
        });
      }
    }
  }

  // ③ needs 自身的完整性：release 必须真的依赖聚合门禁，而不是只看几个构建 job。
  if (!release.needs.includes('gate')) {
    violations.push({
      finding: 'F-05',
      message: 'release 的 needs 里没有聚合门禁 job "gate"；release 与必需检查之间没有依赖。',
    });
  }

  // ④ gate 自己必须把全部必需 job 作为 needs（否则 gate 的聚合是残缺的）。
  const expectedGateNeeds = Object.keys(jobs).filter(
    (name) => name !== 'gate'
      && name !== 'release'
      && !jobs[name].if.includes('is_release')
      // 事件条件 job（只在 pull_request 上跑、或 fork PR 跳过）不能进 gate：
      // gate 要求每个 needs 都 success，这类 job 在别的触发下必然是 skipped。
      && !/github\.event_name|github\.event\.pull_request/.test(jobs[name].if),
  );
  const gateMissing = expectedGateNeeds.filter((n) => !gate.needs.includes(n));
  if (gateMissing.length > 0) {
    violations.push({
      finding: 'F-05',
      message: `CI Gate 没有把全部必需 job 纳入 needs：[${gateMissing.join(', ')}]。`,
    });
  }

  return violations;
}

/** 发版脚本必须在合并前被真实执行过（F-06）。 */
const RELEASE_ASSETS_SCRIPT = 'scripts/ci/build-release-assets.sh';
const RELEASE_ASSETS_TEST = 'scripts/ci/test-release-assets.sh';

function checkReleaseLogicRunsOnPr(root) {
  const ci = readWorkflow(root, '.github/workflows/ci.yml');
  if (ci === null) return [{ finding: 'F-06', message: '找不到 .github/workflows/ci.yml' }];

  const jobs = parseJobs(ci);
  const release = jobs.release;

  // 光有脚本文件、没人真跑它的测试，等于没验证：脚本本身和行为测试都必须存在。
  for (const rel of [RELEASE_ASSETS_SCRIPT, RELEASE_ASSETS_TEST]) {
    if (!fs.existsSync(path.join(root, rel))) {
      return [{
        finding: 'F-06',
        message: `发版纯逻辑必须抽成可在 PR 上执行的脚本 ${rel} 并配真实行为测试，当前不存在`
          + '（逻辑内联在 release job 的 step 里、PR 上恒为 skipped）。',
      }];
    }
  }

  const violations = [];

  if (release && !release.bodyText.includes('build-release-assets.sh')) {
    violations.push({ finding: 'F-06', message: `release job 没有调用 ${RELEASE_ASSETS_SCRIPT}` });
  }

  // 必须有一个“非 master-only”的 job 真的执行那个**行为测试**（而不是随便引用脚本）。
  // 只看有没有引用脚本文件是不够的：空脚本 + 一个 echo 调用也能骗过字符串检查。
  const runsTestOnPr = Object.values(jobs).filter((job) => {
    // 必须真的执行脚本（bash/sh + 路径），仅仅在 echo 字符串里提到文件名不算。
    if (!/(?:^|[\s;&|])(?:bash|sh)\s+\S*test-release-assets\.sh/.test(job.bodyText)) return false;
    // master-only 的判据：if 里出现 is_release。
    return !/is_release/.test(job.if);
  });
  if (runsTestOnPr.length === 0) {
    violations.push({
      finding: 'F-06',
      message: `${RELEASE_ASSETS_TEST} 没有被任何非 master-only 的 job 执行，PR 上仍然验证不到发版纯逻辑。`,
    });
  }

  // 内联的 jq 组装不允许回流：那正是“PR 上跑不到”的形态。
  if (release && /jq\s+-n/.test(release.bodyText)) {
    violations.push({ finding: 'F-06', message: 'release job 里仍有内联 jq 组装逻辑，PR 上无法验证。' });
  }

  // 非自证：真实执行 —— 行为测试必须能对**生产脚本自己的故障**变红。
  // 只检查“job 里出现了 bash test-release-assets.sh”会被替换成 `exit 0` 的空脚本骗过
  // （windows-red-grok I-3）。这里实际跑一次故障对照。
  const nonSelfCertifying = path.join(root, 'scripts/ci/check-release-logic-tested.sh');
  if (!fs.existsSync(nonSelfCertifying)) {
    violations.push({
      finding: 'F-06',
      message: '缺少 scripts/ci/check-release-logic-tested.sh：无法证明行为测试对生产脚本故障敏感'
        + '（只检查脚本名会被空测试脚本骗过）。',
    });
  } else {
    const result = spawnSync('bash', [nonSelfCertifying], { cwd: root, encoding: 'utf8' });
    if (result.status !== 0) {
      const detail = (result.stderr ?? '').trim().split('\n').slice(-3).join(' / ');
      violations.push({
        finding: 'F-06',
        message: `发版纯逻辑的行为测试对生产脚本故障不敏感（退出码 ${result.status}）：${detail}`,
      });
    }
  }

  return violations;
}

function checkReleaseTrigger(root) {
  const script = path.join(root, 'scripts/ci/resolve-version.sh');
  if (!fs.existsSync(script)) return [{ finding: 'F-22', message: '找不到 scripts/ci/resolve-version.sh' }];

  const cases = [
    {
      label: 'push 到 master 必须发版（正对照）',
      expect: 'true',
      env: { GITHUB_EVENT_NAME: 'push', GITHUB_REF: 'refs/heads/master' },
    },
    {
      label: 'master 上手动 workflow_dispatch 不得发版',
      expect: 'false',
      env: { GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REF: 'refs/heads/master' },
    },
    {
      label: 'pull_request 不得发版（正对照）',
      expect: 'false',
      env: { GITHUB_EVENT_NAME: 'pull_request', GITHUB_REF: 'refs/pull/9/merge', PR_NUMBER: '9' },
    },
  ];

  const violations = [];
  for (const c of cases) {
    const result = spawnSync('bash', [script, '--date', '2026-10-10', '--print-env'], {
      env: {
        ...process.env,
        GITHUB_REPOSITORY: '2746267826/pim-windows',
        GITHUB_RUN_NUMBER: '7',
        GITHUB_SHA: 'abcdef1234567890',
        ...c.env,
      },
      encoding: 'utf8',
    });
    if (result.status !== 0) {
      violations.push({ finding: 'F-22', message: `resolve-version.sh 在场景“${c.label}”下退出码 ${result.status}: ${result.stderr.trim()}` });
      continue;
    }
    const actual = (result.stdout.match(/^is_release=(.*)$/m) ?? [])[1];
    if (actual !== c.expect) {
      violations.push({
        finding: 'F-22',
        message: `场景“${c.label}”：is_release 期望 ${c.expect}，实际 ${actual}。`
          + '（判据只看 GITHUB_REF，没有区分事件类型，因此 master 上手动触发会真的发版。）',
      });
    }
  }

  // force_all：workflow_dispatch 声明了就必须真的被读取。
  // 只看 `on:` 这一段：注释里提到 force_all 不算“读到了它”，声明本身也不算。
  const ci = readWorkflow(root, '.github/workflows/ci.yml') ?? '';
  const onSection = ci.split(/\r?\n/).slice(0, (() => {
    const idx = ci.split(/\r?\n/).findIndex((l) => /^jobs:\s*$/.test(l));
    return idx < 0 ? undefined : idx;
  })()).join('\n');
  if (/^\s{6}force_all:\s*$/m.test(onSection)) {
    // 只看 `on:` 段里的表达式/值引用（`${{ ... force_all ... }}` 或 `inputs.force_all`）。
    const used = /\$\{\{[^}]*force_all[^}]*\}\}|inputs\.force_all/.test(onSection);
    if (!used) {
      violations.push({
        finding: 'F-22',
        message: 'workflow_dispatch 输入 force_all 已声明但工作流从不读取它（死参数：声明了就删掉，或让它真的生效）。',
      });
    }
  }

  return violations;
}

const SHA40 = /\b[0-9a-f]{40}\b/;
const SHA40_EXACT = /^[0-9a-f]{40}$/;

/** 把 workflow 里每个 `- uses:`/`- name:` step 切成块。 */
function stepBlocks(text) {
  const lines = text.split(/\r?\n/);
  const steps = [];
  lines.forEach((line, idx) => {
    if (!/^\s*-\s+(uses|name):/.test(line)) return;
    const indent = line.match(/^\s*/)[0].length;
    const block = [line];
    for (let j = idx + 1; j < lines.length; j += 1) {
      const next = lines[j];
      if (/^\s*$/.test(next)) {
        block.push(next);
        continue;
      }
      if (next.match(/^\s*/)[0].length <= indent && /^\s*-\s/.test(next)) break;
      if (next.match(/^\s*/)[0].length < indent) break;
      block.push(next);
    }
    steps.push({ line: idx + 1, text: block.join('\n') });
  });
  return steps;
}

/** 把 workflow 里每个 `- uses:`/`- name:` step 切成块，便于把 repository 和 ref 配对。 */
function checkoutSteps(text) {
  return stepBlocks(text).filter((step) => /uses:\s*actions\/checkout@/.test(step.text));
}

/**
 * YAML `on:` 段里每个 input 的默认值（缩进 6 的 key + 其后的 `default:` 行）。
 * 用它检查**每一个入口**（workflow_call 与 workflow_dispatch）的默认值，
 * 而不是只看文件里第一处 —— Grok C-1 的假绿正是“只查了 workflow_call 那一处”。
 */
function inputDefaults(text) {
  const result = [];
  let current = null;
  text.split(/\r?\n/).forEach((line, idx) => {
    const key = /^ {6}([A-Za-z0-9_]+):\s*$/.exec(line);
    if (key) {
      current = key[1];
      return;
    }
    const def = /^\s+default:\s*'([^']*)'\s*$/.exec(line);
    if (def && current) result.push({ key: current, value: def[1], line: idx + 1 });
  });
  return result;
}

/**
 * 一个「外部源码 pin」step 是否真的把 checkout 绑定到那个 40 位提交上。
 *
 * 只要求“窗口里同时出现过 SHA 和 git checkout”是不够的：把
 * `git checkout --detach $env:KEYSTATS_COMMIT` 改成 `git checkout main`，
 * SHA 仍留在环境变量里，旧判据照样放行（windows-red-grok I-3 的假绿）。
 * 因此这里要求：① step 里有一个变量被赋成 40 位 SHA 字面量；
 * ② 存在一条 `git ... checkout` 命令行**引用同一个变量**；
 * ③ 存在 `rev-parse HEAD`，且同一 step 里把该变量当作比较对象。
 */
function pinnedCheckoutBinding(stepText) {
  const clean = stepText.split(/\r?\n/).filter((line) => !/^\s*#/.test(line));
  const text = clean.join('\n');
  const binds = [
    ...text.matchAll(/^[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*:[ \t]*([0-9a-f]{40})[ \t]*$/gm),
  ].map((m) => ({ name: m[1], sha: m[2] }));

  for (const { name } of binds) {
    const token = new RegExp(`\\$\\{?(?:env:)?${name}\\}?\\b`);
    const checkoutLine = clean.find((l) => /\bgit\b[^\n]*\bcheckout\b/.test(l) && token.test(l));
    const revParse = clean.some((l) => /rev-parse\s+HEAD/.test(l));
    const compared = clean.some(
      (l) => token.test(l) && /(-ne|-eq|!=|==|\$actual|\bactual\b)/.test(l),
    );
    if (checkoutLine && revParse && compared) return { name, checkoutLine };
  }

  return null;
}

function checkSupplyChainPins(root) {
  const violations = [];
  const files = workflowFiles(root);
  // 收集“检出后被复核”的 ref 输入名：同一 step 里同时有 rev-parse HEAD 与该 input 引用。
  const verifiedRefSteps = [];

  for (const file of files) {
    const text = readWorkflow(root, path.join('.github', 'workflows', file));
    if (text === null) continue;
    const lines = text.split(/\r?\n/);
    // 同文件里有没有“拒绝非 40 位 ref”的 fail-closed 验证 step（40 组 [0-9a-f] + exit 1）。
    const guarded = /(\[0-9a-f\]){40}/.test(text.replace(/[ \t]/g, ''));
    const blocks = stepBlocks(text);

    for (const block of blocks) {
      if (!/rev-parse\s+HEAD/.test(block.text)) continue;
      for (const m of block.text.matchAll(/\$\{\{\s*inputs\.([A-Za-z0-9_]+)\s*\}\}/g)) {
        verifiedRefSteps.push({ file, name: m[1], line: block.line });
      }
    }

    lines.forEach((line, idx) => {
      // 外部 git clone：必须把 checkout 真正绑定到 40 位提交。
      // 仓库 URL 可能来自变量（$env:KEYSTATS_REPO），所以这里只看 `git clone` 本身，
      // 不要求 URL 与命令在同一行。
      if (!/^\s*git clone\b/.test(line)) return;
      const containing = blocks.find(
        (b) => b.line <= idx + 1 && idx + 1 <= b.line + b.text.split(/\r?\n/).length,
      );
      const window = containing
        ? containing.text
        : lines
            .slice(idx, idx + 24)
            .filter((l) => !/^\s*#/.test(l))
            .join('\n');
      if (!pinnedCheckoutBinding(window)) {
        violations.push({
          finding: 'F-08',
          message: `${file}:${idx + 1} 从外部仓 git clone 后没有把 checkout 绑定到固定的 40 位提交`
            + '（只要求“窗口里出现过 SHA”会漏掉 `git checkout main`；注释里的 SHA 也不算）。',
          evidence: line.trim(),
        });
      }
    });

    // 外部 checkout：repository 指向外部仓时，ref 必须被固定（字面 SHA 或 fail-closed 验证）。
    for (const step of checkoutSteps(text)) {
      const repoLine = step.text.match(/^\s*repository:\s*(.+)$/m);
      const refLine = step.text.match(/^\s*ref:\s*(.+)$/m);
      if (!repoLine) continue; // 本仓检出，无需 pin

      const repoValue = repoLine[1].trim();
      const refValue = refLine ? refLine[1].trim() : '';
      // 只有当“确实会检出外部仓”时才判：repository 是字面外部仓，或它来自一个可被赋值的外部仓输入。
      const resolvesExternal = /^\$\{\{\s*inputs\.[A-Za-z0-9_]+/.test(repoValue)
        || (!repoValue.startsWith('$') && repoValue.length > 0);
      if (!resolvesExternal) continue;
      if (SHA40.test(refValue)) continue;

      // ref 是互锁输入时，同一个 workflow 里存在“拒绝非 40 位 ref”的 fail-closed 验证
      // 才算固定；写死的可变分支名（如 'main'）不因别处有验证而放行。
      // 带 `||` 兜底的表达式也不算固定：验证 step 检查的是输入值，兜底分支可以让
      // 实际检出的仍是可变 ref。
      const refIsInput = /^\$\{\{\s*inputs\./.test(refValue);
      const hasFallback = /\|\|/.test(refValue);
      const refName = (/\$\{\{\s*inputs\.([A-Za-z0-9_]+)\s*\}\}/.exec(refValue) ?? [])[1];
      // 检出后必须真的复核同一个 ref 输入（rev-parse HEAD + 与 inputs.<ref> 比较），
      // 否则“有验证 step”可能验的是别的东西，实际检出的仍是任意提交。
      const rebound = refName
        ? verifiedRefSteps.some((v) => v.file === file && v.name === refName)
        : false;
      if (refIsInput && !hasFallback && guarded && rebound) continue;

      violations.push({
        finding: 'F-09',
        message: `${file}:${step.line} 外部仓 checkout 使用了可变 ref（${refValue || '<缺省>'}），`
          + '且没有“拒绝非 40 位 ref + 检出后复核同一个 ref 输入”的 fail-closed 验证；'
          + '安装器会打包随时间变化的上游代码。',
        evidence: `${repoValue} @ ${refValue || '<缺省>'}`,
      });
    }

    // checkout 落在 $GITHUB_WORKSPACE 下，而 job 可能有 defaults.run.working-directory：
    // 用相对路径 `git -C .extension-site` 会在错误的 cwd 下 exit 128（Grok C-1）。
    const checkoutPaths = checkoutSteps(text)
      .map((step) => (/^\s*path:\s*(\S+)\s*$/m.exec(step.text) ?? [])[1])
      .filter((p) => p && !p.startsWith('$') && !p.startsWith('/'));
    for (const block of blocks) {
      const usesWorkspaceCwd = /working-directory:\s*\$\{\{\s*github\.workspace\s*\}\}/.test(block.text);
      for (const line of block.text.split(/\r?\n/)) {
        const m = /\bgit\s+-C\s+("[^"]+"|'[^']+'|\S+)/.exec(line);
        if (!m) continue;
        const arg = m[1].replace(/^["']|["']$/g, '');
        if (arg.startsWith('$') || arg.startsWith('/')) continue;
        if (!checkoutPaths.includes(arg)) continue;
        if (usesWorkspaceCwd) continue;
        violations.push({
          finding: 'F-09',
          message: `${file}:${block.line} 用相对路径 \`git -C ${arg}\` 读取 checkout 出来的仓库，`
            + '但该 step 的 cwd 不一定是 $GITHUB_WORKSPACE（job 级 working-directory 会让它 exit 128）；'
            + '请改成绝对路径或显式设置 working-directory。',
          evidence: line.trim(),
        });
      }
    }

    // 缺失即失败：KeyStats 源码找不到时必须让 job 红，而不是 warning + 继续。
    if (/KeyStats source not found/.test(text)) {
      const at = text.indexOf('KeyStats source not found');
      const window = text.slice(Math.max(0, at - 400), at + 400);
      if (/Write-Warning/.test(window) && !/exit 1/.test(window)) {
        violations.push({
          finding: 'F-08',
          message: `${file}: KeyStats 源码缺失时只 Warning 后继续（fail-open），安装包会静默缺少组件而 CI 全绿。`,
        });
      }
    }

    // 插件 ref 的**每一个**入口（workflow_call / workflow_dispatch）默认值都不得是可变分支名。
    for (const entry of inputDefaults(text)) {
      if (entry.key !== 'site_extension_ref' && entry.key !== 'extension_ref') continue;
      if (entry.value === '' || SHA40_EXACT.test(entry.value)) continue;
      violations.push({
        finding: 'F-09',
        message: `${file}:${entry.line} ${entry.key} 的默认值是可变 ref "${entry.value}"；`
          + '每个入口都必须留空（用本仓源码）或写 40 位提交号。',
      });
    }
  }

  return violations;
}

function readCompileList(csprojPath) {
  const text = fs.readFileSync(csprojPath, 'utf8');
  return [...text.matchAll(/<Compile Include="([^"]+)"/g)].map((m) => m[1]);
}

/** 从 csproj 的显式 Compile 清单里读出磁盘上真实存在的文件（绝对路径）。 */
function compiledFilesOnDisk(root, csprojRel) {
  const abs = path.join(root, csprojRel);
  if (!fs.existsSync(abs)) return [];
  const dir = path.dirname(abs);
  return readCompileList(abs)
    .map((rel) => path.normalize(path.join(dir, rel)))
    .filter((p) => fs.existsSync(p));
}

/**
 * F-03：请求契约一致性必须有机械约束，并且在必需 job 里**真实执行全量套件**。
 *
 * 两道口子一起守：
 *   ① 必需 job 里必须真的跑契约测试 —— 不能只有一份没人读的 openapi.json；
 *   ② 那个 job 不能把全量测试套件换成 `--filter`：审计 I-5 证明，一旦允许 filter，
 *      把套件缩成只跑某几个类就能让名义上的门禁失去覆盖，而检查仍然变绿。
 */
function checkContractEnforcement(root) {
  const violations = [];

  // ① 必须有测试真的读取契约快照（而不是只有一份没人读的 openapi.json）。
  const readers = [];
  const walk = (dir) => {
    if (!fs.existsSync(dir)) return;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (['bin', 'obj', 'node_modules', '.git'].includes(entry.name)) continue;
        walk(abs);
      } else if (entry.name.endsWith('.cs') || entry.name.endsWith('.mjs') || entry.name.endsWith('.sh')) {
        const text = fs.readFileSync(abs, 'utf8');
        if (/openapi\.json/.test(text) && /contract/.test(text)) readers.push(path.relative(root, abs));
      }
    }
  };
  walk(path.join(root, 'tests'));
  walk(path.join(root, 'scripts'));

  if (readers.length === 0) {
    violations.push({
      finding: 'F-03',
      message: '没有任何测试或脚本读取 contract/openapi.json —— 契约快照与手写 DTO 的偏差不会被任何检查拦住。',
    });
  }

  const ci = readWorkflow(root, '.github/workflows/ci.yml');
  if (ci === null) {
    violations.push({ finding: 'F-03', message: '找不到 .github/workflows/ci.yml' });
    return violations;
  }
  const jobs = parseJobs(ci);
  const requiredJobs = jobs.gate ? jobs.gate.needs : [];

  // ② 必需 job 里必须有一个在跑 Pim.Client.Tests 的 dotnet test。
  const enforcing = requiredJobs.filter((name) => {
    const job = jobs[name];
    if (!job) return false;
    return /Pim\.Client\.Tests/.test(job.bodyText) && /dotnet\s+test/.test(job.bodyText);
  });

  if (enforcing.length === 0) {
    violations.push({
      finding: 'F-03',
      message: 'CI 的必需 job 里没有任何一个执行 Pim.Client.Tests 的 dotnet test'
        + '（契约偏差必须能让 CI 变红，否则 F-01/F-02 这类问题会再次静默发生）。',
    });
  }

  // ③ 那个 job 不得用 --filter 把全量套件缩小。
  for (const name of enforcing) {
    for (const match of jobs[name].bodyText.matchAll(/dotnet\s+test[^\n]*/g)) {
      if (/--filter/.test(match[0])) {
        violations.push({
          finding: 'F-03',
          message: `必需 job "${name}" 用 --filter 缩窄了测试范围：${match[0].trim()}。`
            + '门禁必须跑完整测试工程，否则把套件换成只跑某几个类也能“变绿”。',
        });
      }
    }
  }

  // ④ 契约/心跳相关用例必须是磁盘上真实存在的文件（csproj 写名字但没文件不算）。
  const compiled = compiledFilesOnDisk(root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj');
  const realContractCases = compiled.filter((p) => /Contract|Heartbeat|PlannedOffline|Tracker/.test(path.basename(p)));
  if (realContractCases.length === 0) {
    violations.push({
      finding: 'F-03',
      message: 'tests/Pim.Client.Tests 的 Compile 清单里没有任何契约/心跳/上报相关的用例文件在磁盘上真实存在。',
    });
  }

  return violations;
}

/**
 * F-10：契约形状/心跳队列用例必须**真的被编译**并在必需 job 里执行。
 *
 * 审计 I-5 的教训：旧检查只认两个文件名是否出现在 csproj 的 XML 里 ——
 * 在 csproj 写上文件名、磁盘上没有该文件，检查照样通过。所以这里以
 * 「csproj 清单 ∩ 磁盘真实文件」为准，并要求必需 job 跑的是**完整**测试工程。
 */
function checkContractRoundTripCompiled(root) {
  const violations = [];
  const csprojRel = 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj';
  const compiled = compiledFilesOnDisk(root, csprojRel);
  const names = new Set(compiled.map((p) => path.basename(p)));

  const requiredClasses = {
    'ContractWireShapeTests.cs': '契约字段集合/类型',
    'HeartbeatQueueDepthTests.cs': '心跳队列积压',
  };
  for (const [file, why] of Object.entries(requiredClasses)) {
    if (!names.has(file)) {
      violations.push({
        finding: 'F-10',
        message: `${file}（${why}）没有被 ${csprojRel} 编译，或 csproj 列了名字但磁盘上没有这个文件 —— `
          + '这类用例最容易变成“不参与执行的孤儿”。',
      });
    }
  }

  // csproj 引用了磁盘上不存在的文件：写名字不等于编译。
  const csprojAbs = path.join(root, csprojRel);
  if (fs.existsSync(csprojAbs)) {
    for (const rel of readCompileList(csprojAbs)) {
      const target = path.normalize(path.join(path.dirname(csprojAbs), rel));
      if (!fs.existsSync(target)) {
        violations.push({
          finding: 'F-10',
          message: `${csprojRel} 的 Compile 清单引用了磁盘上不存在的文件 ${rel}（写文件名不等于真的参与编译）。`,
        });
      }
    }
  }

  // 必须有一个必需 job 真的跑完整测试工程（不是 filter 出来的子集）。
  const ci = readWorkflow(root, '.github/workflows/ci.yml');
  const jobs = ci ? parseJobs(ci) : {};
  const requiredJobs = jobs.gate ? jobs.gate.needs : [];
  const runsFullSuite = requiredJobs.some((name) => {
    const body = jobs[name]?.bodyText ?? '';
    return /Pim\.Client\.Tests/.test(body)
      && /dotnet\s+test/.test(body)
      && !/--filter/.test(body);
  });
  if (!runsFullSuite) {
    violations.push({
      finding: 'F-10',
      message: '没有任何必需 job 跑 Pim.Client.Tests 的完整套件（无 --filter）—— 孤儿用例即使被编译也验证不到。',
    });
  }

  return violations;
}

/**
 * F-16：README 声明的用例文件数必须与 csproj 实际清单一致。
 *
 * 故意只做“数字对不对”，不去镜像每个文件名单 —— 名单本身由
 * checkContractRoundTripCompiled 与真实编译保证。
 */
function checkTestInventory(root) {
  const violations = [];
  const readmePath = path.join(root, 'tests', 'ClientWindows', 'README.md');
  if (!fs.existsSync(readmePath)) return [{ finding: 'F-16', message: '找不到 tests/ClientWindows/README.md' }];
  const readme = fs.readFileSync(readmePath, 'utf8');

  const projects = [
    { name: 'Pim.Client.Tests', csproj: path.join(root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj') },
    { name: 'Pim.Client.App.Tests', csproj: path.join(root, 'tests/Pim.Client.App.Tests/Pim.Client.App.Tests.csproj') },
  ];

  for (const project of projects) {
    if (!fs.existsSync(project.csproj)) continue;
    const list = readCompileList(project.csproj);
    // 只认「项目名 + N 个文件」这组声明，不解析表格布局。
    const claim = new RegExp(`\`?${project.name.replace('.', '\\.')}\`?[^\\n]*?(\\d+)\\s*个文件`).exec(readme);
    const countMatch = claim ? [claim[0], claim[1]] : null;
    if (countMatch && Number(countMatch[1]) !== list.length) {
      violations.push({
        finding: 'F-16',
        message: `tests/ClientWindows/README.md 声称 ${project.name} 覆盖 ${countMatch[1]} 个文件，csproj 实际列了 ${list.length} 个。`,
      });
    }
  }

  return violations;
}


/**
 * F-12：根 `.github/workflows/` 必须在**会跑的触发条件**下执行静态检查。
 *
 * 检查“文件里出现过 prettier --check 这个字符串”是不够的：写在一个永不触发的 job 里
 * 等于没有。这里要求 prettier 出现在一个带 `pull_request` 触发的工作流中。
 * CodeQL 与第三方 Action 的可用性无法在本地验证，保持 NOT-VERIFIED（见 requirements）。
 */
function checkRootStaticAnalysis(root) {
  const violations = [];
  const files = workflowFiles(root);

  // prettier 必须在**会随 pull_request 执行**的 job 里，而不是只出现在某个 workflow 的文本里
  // （嵌套目录 / 永不触发的 job 都等于没有风格门禁）。
  const prettierJobs = [];
  for (const file of files) {
    const text = readWorkflow(root, path.join('.github', 'workflows', file)) ?? '';
    if (!/^\s{2}pull_request:/m.test(text)) continue;
    for (const [name, job] of Object.entries(parseJobs(text))) {
      if (!/prettier\s+--check/.test(job.bodyText)) continue;
      // job 级条件如果把 PR 排除在外，就不算“在 PR 上执行”。
      if (/github\.event_name\s*==\s*'push'/.test(job.if) || /github\.event_name\s*!=\s*'pull_request'/.test(job.if)) continue;
      prettierJobs.push(`${file}:${name}`);
    }
  }
  if (prettierJobs.length === 0) {
    violations.push({
      finding: 'F-12',
      message: '根 .github/workflows/ 下没有任何在 pull_request 上执行的 prettier --check job'
        + '（只出现在文本里、或写在永不触发的 job / 嵌套目录里，都等于没有静态检查）。',
    });
  }

  // CodeQL 必须真的接线到根 workflow，并且**参与某个门禁**（gate 或 release 的 needs）。
  const checkJobs = [];
  for (const file of files) {
    const text = readWorkflow(root, path.join('.github', 'workflows', file)) ?? '';
    for (const [name, job] of Object.entries(parseJobs(text))) {
      if (/github\/codeql-action\/\w+@/.test(job.bodyText)) checkJobs.push({ file, name, job });
    }
  }
  if (checkJobs.length === 0) {
    violations.push({
      finding: 'F-12',
      message: '根 .github/workflows/ 下没有 CodeQL 静态安全扫描 job（嵌套目录里的永远不执行）。',
    });
  } else {
    const ci = readWorkflow(root, '.github/workflows/ci.yml') ?? '';
    const jobs = parseJobs(ci);
    const gated = new Set([...(jobs.gate?.needs ?? []), ...(jobs.release?.needs ?? [])]);
    for (const entry of checkJobs) {
      if (entry.file !== 'ci.yml' || !gated.has(entry.name)) {
        violations.push({
          finding: 'F-12',
          message: `CodeQL job ${entry.file}:${entry.name} 没有参与任何门禁（既不在 gate.needs 也不在 release.needs）——`
            + '“装配了但没人消费”与永不执行等价。',
        });
      }
    }
  }

  const nested = path.join(root, 'browser-extension', '.github', 'workflows');
  if (fs.existsSync(nested)) {
    const names = fs.readdirSync(nested).filter((f) => f.endsWith('.yml'));
    if (names.length > 0) {
      violations.push({
        finding: 'F-12',
        message: `browser-extension/.github/workflows/ 下仍有 ${names.join(', ')} —— GitHub 永不执行嵌套目录里的工作流，`
          + '会被误读为“本仓有 lint / CodeQL 门禁”。',
      });
    }
  }

  return violations;
}

/**
 * F-13 / F-14 / F-15 / F-17 / F-19：流程文档必须与工作流的**实际行为**一致。
 *
 * 这里刻意只守住“文档写了什么、实现就是什么”的可核对关系，不镜像整份文档文本：
 *   ① 文档里引用的仓内路径必须存在（断链就是断链，补一个空文件不算修复）；
 *   ② AGENTS.md 指定的本地门禁命令必须真的能执行（交给 check-dependency-hygiene.sh 实跑，
 *      这里只要求它别把「根本不构建」的 .slnx 当作门禁命令）；
 *   ③ docs-only 的说法必须与 ci.yml 的触发器一致：有 paths 过滤才允许说会跳过构建；
 *   ④ README 必须列出实际存在的每个 workflow，不能漏掉 ci.yml 这种真正管门禁的文件；
 *   ⑤ PR 模板引用的流程文档必须存在。
 */
function checkDocsConsistency(root) {
  const violations = [];
  const readIfExists = (rel) => {
    const abs = path.join(root, rel);
    return fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
  };

  // F-13 / F-14：文档里引用的仓内路径必须存在。
  const docRefs = [
    { file: 'AGENTS.md', pattern: /`(scripts\/[^`]+\.(?:sh|ps1|mjs))`/g },
    { file: '.github/pull_request_template.md', pattern: /(docs\/[A-Za-z0-9._/-]+\.md)/g },
  ];
  for (const { file, pattern } of docRefs) {
    const text = readIfExists(file);
    if (text === null) continue;
    for (const match of text.matchAll(pattern)) {
      const referenced = match[1];
      if (!fs.existsSync(path.join(root, referenced))) {
        violations.push({
          finding: referenced.includes('docs/') ? 'F-14' : 'F-13',
          message: `${file} 引用了仓内不存在的路径 ${referenced}（断链引用）。`,
        });
      }
    }
  }

  const ciText = readIfExists('.github/workflows/ci.yml') ?? '';
  const agents = readIfExists('AGENTS.md') ?? '';

  // F-15：docs-only 跳过构建的说法必须与触发器一致。
  // 只有真的加了 paths 过滤，才允许声称 docs-only 会跳过平台构建。
  const claimsSkip = /docs-only.{0,40}(skip|跳过)/i.test(agents);
  const hasPathsFilter = /^\s*paths(-ignore)?:/m.test(ciText);
  if (claimsSkip && !hasPathsFilter) {
    violations.push({
      finding: 'F-15',
      message: 'AGENTS.md 声称 docs-only 合并会跳过平台构建，但 ci.yml 的触发器没有任何 paths 过滤（文档与实现相反）。',
    });
  }
  if (!claimsSkip && hasPathsFilter) {
    violations.push({
      finding: 'F-15',
      message: 'ci.yml 已经加了 paths 过滤，但 AGENTS.md 没有说明 docs-only 合并会跳过构建（文档落后于实现）。',
    });
  }

  // F-19：README 的 workflow 清单必须覆盖 .github/workflows/ 下全部文件。
  const readme = readIfExists('README.md') ?? '';
  for (const file of workflowFiles(root)) {
    if (!readme.includes(file)) {
      violations.push({ finding: 'F-19', message: `README.md 没有列出工作流 ${file}，读者会低估实际门禁与产物范围。` });
    }
  }

  // F-17：AGENTS.md 不得把“本机根本构建不了、也不覆盖测试”的 .slnx 当作提交前门禁命令。
  // 只认真正被写成命令的形式（`dotnet ... .slnx ...` 代码片段）；文档里解释
  // “slnx 不是门禁命令”的说明性文字不算违规。
  // 真正的可执行性由 check-dependency-hygiene.sh 逐字执行 AGENTS.md 里的 dotnet 命令来验证。
  for (const match of agents.matchAll(/`(dotnet [^`]*\.slnx[^`]*)`/g)) {
    violations.push({
      finding: 'F-17',
      message: 'AGENTS.md 仍把 src/client-windows/Pim.Client.Windows.slnx 写成要执行的 dotnet 命令：'
        + '该 slnx 只含产品工程（不含测试工程），且在当前 SDK 上是 error MSB4068。',
      evidence: match[1],
    });
  }

  return violations;
}

function checkWebViewTokenPolicy(root) {
  const violations = [];
  const srcRoot = path.join(root, 'src');
  if (!fs.existsSync(srcRoot)) return violations;

  const files = [];
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === 'bin' || entry.name === 'obj') continue;
        walk(abs);
      } else if (entry.name.endsWith('.cs')) {
        files.push(abs);
      }
    }
  };
  walk(srcRoot);

  for (const abs of files) {
    const rel = path.relative(root, abs);
    const text = fs.readFileSync(abs, 'utf8');
    text.split(/\r?\n/).forEach((line, idx) => {
      // 注释行不是执行路径，单独跳过（否则代码注释里的说明会被当成违规）。
      const isComment = /^\s*(\/\/|\/\*|\*)/.test(line);
      if (isComment) return;

      const reasons = [];
      const push = (reason) => reasons.push(reason);

      if (/ExecuteScriptAsync/.test(line) && /(token|Token)/.test(line)) {
        push('把令牌值交给 WebView 的 ExecuteScriptAsync（AGENTS.md B5：tokens 绝不进 WebView）');
      }
      if (/sessionStorage\.setItem\(\s*['"]accessToken['"]/.test(line)) {
        push('把 access token 写进 WebView 的 sessionStorage，页面内任意脚本/XSS 都能读到');
      }
      if (/__pimToken/.test(line)) {
        push('通过 window.__pimToken 把令牌暴露给被导航页面');
      }

      if (reasons.length > 0) {
        violations.push({
          finding: 'F-11',
          message: `${rel}:${idx + 1} ${reasons.join('；')}。`,
          evidence: line.trim(),
        });
      }
    });
  }

  return violations;
}

function collectSources(root) {
  const sources = [];
  const walk = (dir) => {
    if (!fs.existsSync(dir)) return;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (['bin', 'obj', 'node_modules', '.git'].includes(entry.name)) continue;
        walk(abs);
      } else if (entry.name.endsWith('.cs')) {
        sources.push({ abs: path.resolve(abs), text: fs.readFileSync(abs, 'utf8') });
      }
    }
  };
  walk(path.join(root, 'src'));
  return sources;
}

function checkDeadCode(root) {
  const violations = [];
  const sources = collectSources(root);

  // F-21 的验收允许“有显式豁免记录”：豁免必须逐条写明理由，不能靠静默删除断言。
  const allowlistPath = path.join(root, 'scripts/ci/dead-code-allowlist.json');
  let exempt = new Map();
  if (fs.existsSync(allowlistPath)) {
    const parsed = JSON.parse(fs.readFileSync(allowlistPath, 'utf8'));
    for (const entry of parsed.exempt ?? []) {
      exempt.set(entry.member, entry.reason ?? '<未写理由>');
    }
  } else {
    exempt = null;
  }

  const exempted = (label, name) => {
    if (exempt === null) return false;
    const key = `${label}${name}`;
    if (!exempt.has(key)) return false;
    if (exempt.get(key) === '<未写理由>') {
      violations.push({
        finding: 'F-21',
        message: `豁免记录 ${key} 没有写明理由（豁免必须可追溯，不能变成无理由的静默忽略）。`,
      });
    }
    return true;
  };

  const checkMembers = (fileRel, pattern, label) => {
    const abs = path.resolve(path.join(root, fileRel));
    if (!fs.existsSync(abs)) return;
    const text = fs.readFileSync(abs, 'utf8');
    const names = [...text.matchAll(pattern)].map((m) => m[1]);

    for (const name of new Set(names)) {
      // 调用者统计：
      //  - 其它文件里任何 `Name(` / `Name<` 都算使用；
      //  - **声明文件内部**只把裸自调用（`Name(` / `this.Name(`）算使用，
      //    否则 `_httpClient.DeleteAsync(...)` 这类“同名但不同接收者”的调用会把死代码误判成活代码。
      const bare = new RegExp(`(?<![.\\w])${name}\\s*[(<]`, 'g');
      const selfCall = new RegExp(`this\\.${name}\\s*[(<]`, 'g');
      const anyCall = new RegExp(`\\b${name}\\s*[(<]`, 'g');

      const occurrences = sources.reduce((sum, source) => {
        const isDeclaringFile = source.abs === abs;
        const pattern = isDeclaringFile ? null : anyCall;
        const hits = isDeclaringFile
          ? (source.text.match(bare) ?? []).length + (source.text.match(selfCall) ?? []).length
          : (source.text.match(pattern) ?? []).length;
        return sum + hits;
      }, 0);

      if (occurrences <= 1) {
        if (exempted(label, name)) continue;
        violations.push({
          finding: 'F-21',
          message: `${label}${name} 在全仓 src/ 下没有任何调用者，也没有显式豁免记录`
            + '（死代码会制造“看起来有覆盖”的假象；审计要求删除、补调用方或显式登记豁免）。',
        });
      }
    }
  };

  checkMembers(
    'src/client-windows/Pim.Client.Core/Services/ApiClient.cs',
    /public\s+(?:static\s+|async\s+|override\s+)*[\w.<>?,\[\]]+\s+([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(/g,
    'ApiClient.',
  );
  checkMembers(
    'src/client-windows/Pim.Client.Core/Services/AuthService.cs',
    /public\s+(?:static\s+|async\s+|override\s+)*[\w.<>?,\[\]]+\s+([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(/g,
    'AuthService.',
  );

  // 死配置 / 死脚本：仓库里存在但没有任何引用。
  const allText = sources.map((s) => s.text).join('\n');
  for (const [name, rel] of [
    ['AwBaseUrl', 'src/client-windows/Pim.Client.Core/ClientDefaults.cs'],
    ['resolve-version.ps1', 'scripts/ci/resolve-version.ps1'],
  ]) {
    if (!fs.existsSync(path.join(root, rel))) continue;
    // 死代码被**删除**（审计允许的修法之一）之后这里就没有可判定的成员了：
    // 只有该成员仍声明在文件里时才继续判“有没有引用”。
    if (name === 'AwBaseUrl' && !/AwBaseUrl\s*=>/.test(fs.readFileSync(path.join(root, rel), 'utf8'))) {
      continue;
    }
    const referenced = name === 'AwBaseUrl'
      ? /\.AwBaseUrl\b|AwBaseUrl\s*=/.test(allText) && (allText.match(/AwBaseUrl/g) ?? []).length > 1
      : new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).test(
        workflowFiles(root).map((f) => readWorkflow(root, path.join('.github', 'workflows', f)) ?? '').join('\n'),
      );
    if (!referenced) {
      const label = name === 'AwBaseUrl' ? 'ClientDefaults.' : '';
      if (exempted(label, name)) continue;
      violations.push({
        finding: 'F-21',
        message: `${rel} 中的 ${name} 在全仓没有任何引用，也没有显式豁免记录。`,
      });
    }
  }

  return violations;
}

export const CHECKS = [
  { id: 'contract-enforcement', findings: ['F-03'], run: checkContractEnforcement },
  { id: 'contract-roundtrip-compiled', findings: ['F-10'], run: checkContractRoundTripCompiled },
  { id: 'release-gate', findings: ['F-05'], run: checkReleaseGate },
  { id: 'release-logic-runs-on-pr', findings: ['F-06'], run: checkReleaseLogicRunsOnPr },
  { id: 'release-trigger', findings: ['F-22'], run: checkReleaseTrigger },
  { id: 'supply-chain-pins', findings: ['F-08', 'F-09'], run: checkSupplyChainPins },
  { id: 'test-inventory', findings: ['F-10', 'F-16'], run: checkTestInventory },
  { id: 'root-static-analysis', findings: ['F-12'], run: checkRootStaticAnalysis },
  { id: 'docs-consistency', findings: ['F-13', 'F-14', 'F-15', 'F-17', 'F-19'], run: checkDocsConsistency },
  { id: 'webview-token-policy', findings: ['F-11'], run: checkWebViewTokenPolicy },
  { id: 'dead-code', findings: ['F-21'], run: checkDeadCode },
];

function main(argv) {
  let root = REPO_ROOT;
  let only = null;
  let asJson = false;
  const exclude = new Set();

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--root') root = path.resolve(argv[++i]);
    else if (arg === '--check') only = argv[++i];
    else if (arg === '--exclude') {
      for (const id of argv[++i].split(',').map((v) => v.trim()).filter(Boolean)) exclude.add(id);
    } else if (arg === '--json') asJson = true;
    else if (arg === '--list') {
      for (const c of CHECKS) console.log(`${c.id}\t${c.findings.join(',')}`);
      return 0;
    } else {
      console.error(`未知参数: ${arg}`);
      return 2;
    }
  }

  const unknownExcluded = [...exclude].filter((id) => !CHECKS.some((c) => c.id === id));
  if (unknownExcluded.length > 0) {
    console.error(`--exclude 里有未注册的检查: ${unknownExcluded.join(', ')}`);
    return 2;
  }

  const selected = (only ? CHECKS.filter((c) => c.id === only) : CHECKS)
    .filter((c) => !exclude.has(c.id));
  if (selected.length === 0) {
    console.error(`没有可执行的检查（--check=${only ?? '<all>'} --exclude=${[...exclude].join(',') || '<none>'}）`);
    return 2;
  }
  for (const id of exclude) {
    // 被排除的检查必须显式可见：不允许静默地少跑一条门禁。
    console.log(`SKIP ${id}（显式 --exclude；排除理由必须写进对应的 round 报告）`);
  }

  const report = [];
  let failed = 0;
  for (const check of selected) {
    let violations;
    try {
      violations = check.run(root) ?? [];
    } catch (error) {
      violations = [{ finding: check.findings[0], message: `检查 ${check.id} 执行失败: ${error.message}` }];
    }
    report.push({ check: check.id, findings: check.findings, violations });
    if (violations.length > 0) failed += 1;
  }

  if (asJson) {
    console.log(JSON.stringify({ root, failed, report }, null, 2));
  } else {
    for (const entry of report) {
      if (entry.violations.length === 0) {
        console.log(`PASS ${entry.check} (${entry.findings.join(',')})`);
        continue;
      }
      console.log(`FAIL ${entry.check} (${entry.findings.join(',')}) — ${entry.violations.length} 项违反`);
      for (const v of entry.violations) {
        console.log(`  [${v.finding}] ${v.message}`);
        if (v.evidence) console.log(`      evidence: ${v.evidence}`);
      }
    }
    console.log('');
    console.log(failed === 0 ? '全部检查通过' : `${failed}/${report.length} 项检查存在违反`);
  }

  return failed === 0 ? 0 : 1;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  process.exit(main(process.argv.slice(2)));
}
