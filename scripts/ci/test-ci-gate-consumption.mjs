#!/usr/bin/env node
/**
 * REQ-COM-02 / REQ-COM-04 / REQ-COM-05 · 审计整改 **E 项**（round4 建立，round5 按独立 review
 * I-2 / M-9 改写为「gate + release 两侧」的真实判定）。
 *
 * 现状（round3 交付、round4 复核确认）：
 *   - `pr-metadata` 只挂 `pull_request`，且**不在** `gate.needs` 里，gate 也不看它的 result
 *     → 新 PR 缺四段/分支名不合规时，只有 `pr-metadata` 这个 check 变红，
 *       **名为 `CI Gate` 的必需聚合 job 仍然是 success**。
 *   - `codeql` 只在 `release.needs` 里；`if:` 把 fork PR 整个跳过，同仓 PR 上
 *     CodeQL 失败/取消/被跳过**不会**挡 `CI Gate`（只在发版时才挡）。
 *
 * 冻结选择（不登记预期 skip，不放宽 F-05 守卫）：
 *   - pr-metadata 在 workflow 的全部 event 上运行。push / schedule / workflow_dispatch
 *     没有 PR body 时，校验步骤自身 exit 0；pull_request 仍校验标题、四段和分支。
 *   - pr-metadata 与 codeql 同时进入 gate.needs 和 release.needs。
 *     failure / cancelled / skipped / missing 都挡住 gate 和 release。
 *     master 上全部 success 时可以发版。
 *   - codeql 在 PR 与 push 上都分析。fork upload=never，同仓与 master upload=always。
 *     不用 pull_request_target，不依赖 Send write tokens。
 *   - gate 只认 success，不把 skipped 按事件放行，因此 gate 不需要 EVENT_NAME / PR_FROM_FORK。
 *
 * ## 平台/工具说明
 * - gate 的判定是 shell 脚本，这里**真实执行** `.github/workflows/ci.yml` 里那段 `run` 原文
 *   （不重写逻辑）。不用 `@actions/expressions` 近似替代：它只覆盖 `if:` 表达式，
 *   且库内没有平台内建 `always()`。
 * - 未访问 GitHub、未改 ruleset、未把任何未验证项写成 PASS。
 *
 * 用法：`node --test scripts/ci/test-ci-gate-consumption.mjs`
 * （已由既有 `ci-scripts` job 的 `node --test scripts/ci/test-*.mjs` 收集；不新增 job。）
 */

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const CI_YML = '.github/workflows/ci.yml';

/** 事件作用域的新 check（本检查的判定对象）。 */
const EVENT_SCOPED = ['R_PR_METADATA', 'R_CODEQL'];


export function readCi() {
  return fs.readFileSync(path.join(ROOT, CI_YML), 'utf8');
}

/** 取某个 job 的正文行（到下一个顶层键为止）。 */
export function jobBlockLines(ciText, jobName) {
  const lines = ciText.split(/\r?\n/);
  const start = lines.findIndex((line) => new RegExp(`^ {2}${jobName}:\\s*$`).test(line));
  if (start < 0) return null;
  const out = [];
  for (let i = start + 1; i < lines.length; i += 1) {
    if (/^\S/.test(lines[i]) && lines[i].trim() !== '') break;
    out.push(lines[i]);
  }
  return out;
}

/** job 的 needs 数组（支持行内与列表两种写法）。 */
export function jobNeeds(ciText, jobName) {
  const block = jobBlockLines(ciText, jobName) ?? [];
  for (let i = 0; i < block.length; i += 1) {
    const inline = /^ {4}needs:\s*\[(.*)\]\s*$/.exec(block[i]);
    if (inline) {
      return inline[1].split(',').map((s) => s.trim().replace(/^['"]|['"]$/g, '')).filter(Boolean);
    }
    if (/^ {4}needs:\s*$/.test(block[i])) {
      const items = [];
      for (let j = i + 1; j < block.length; j += 1) {
        const item = /^ {6}- (.+?)\s*$/.exec(block[j]);
        if (!item) break;
        items.push(item[1].trim().replace(/^['"]|['"]$/g, ''));
      }
      return items;
    }
  }
  return [];
}

/** job 的 if 表达式（行内值或 `|` 块，块内按空白拼接为一行）。 */
export function jobIfExpression(ciText, jobName) {
  const block = jobBlockLines(ciText, jobName) ?? [];
  for (let i = 0; i < block.length; i += 1) {
    const inline = /^ {4}if:\s*(.+?)\s*$/.exec(block[i]);
    if (inline && inline[1] !== '|' && inline[1] !== '>') return inline[1];
    if (/^ {4}if:\s*[|>]\s*$/.test(block[i])) {
      const parts = [];
      for (let j = i + 1; j < block.length; j += 1) {
        const line = block[j];
        if (line.trim() === '') continue;
        const indent = line.match(/^(\s*)/)[1].length;
        if (indent <= 4) break;
        parts.push(line.trim());
      }
      return parts.join(' ');
    }
  }
  return '';
}

/** job（含其 steps）里出现过的 `env:` 键名。 */
export function jobEnvKeys(ciText, jobName) {
  const block = jobBlockLines(ciText, jobName) ?? [];
  const keys = new Set();
  for (let i = 0; i < block.length; i += 1) {
    const envMatch = /^(\s*)env:\s*$/.exec(block[i]);
    if (!envMatch) continue;
    const envIndent = envMatch[1].length;
    for (let j = i + 1; j < block.length; j += 1) {
      const line = block[j];
      if (line.trim() === '') continue;
      const indent = line.match(/^(\s*)/)[1].length;
      if (indent <= envIndent) break;
      const kv = /^\s+([A-Za-z_][A-Za-z0-9_]*):/.exec(line);
      if (kv) keys.add(kv[1]);
    }
  }
  return keys;
}

/** 从 ci.yml 里取出 `gate` job 的第一个 `run: |` 脚本原文（不做任何改写）。 */
export function extractGateScript(ciText = readCi()) {
  const lines = ciText.split(/\r?\n/);
  const jobStart = lines.findIndex((line) => /^ {2}gate:\s*$/.test(line));
  if (jobStart < 0) throw new Error(`${CI_YML} 里找不到 gate job`);

  let jobEnd = lines.length;
  for (let i = jobStart + 1; i < lines.length; i += 1) {
    if (/^\S/.test(lines[i]) && lines[i].trim() !== '') {
      jobEnd = i;
      break;
    }
  }

  let runIdx = -1;
  for (let i = jobStart; i < jobEnd; i += 1) {
    if (/^\s+run:\s*\|\s*$/.test(lines[i])) {
      runIdx = i;
      break;
    }
  }
  if (runIdx < 0) throw new Error('gate job 里找不到 `run: |` 脚本');

  const indent = lines[runIdx].match(/^(\s*)/)[1].length;
  const script = [];
  for (let i = runIdx + 1; i < jobEnd; i += 1) {
    const line = lines[i];
    const lineIndent = line.match(/^(\s*)/)[1].length;
    if (line.trim() !== '' && lineIndent <= indent) break;
    script.push(line.slice(Math.min(line.length, indent + 2)));
  }
  return script.join('\n');
}

/** gate 脚本里出现过的全部 `R_*` 变量名（用于把“未设置”与“显式失败”分开）。 */
export function knownResultVars(script = extractGateScript()) {
  return [...new Set([...script.matchAll(/\b(R_[A-Z0-9_]+)\b/g)].map((m) => m[1]))].sort();
}

/**
 * 真实执行 gate 脚本。
 * @param {{event?: string, fork?: boolean, results?: Record<string,string>}} scenario
 * @returns {{status: number, stdout: string, stderr: string}}
 */
export function runGate(scenario = {}) {
  const script = extractGateScript();
  const env = { ...process.env };
  for (const name of knownResultVars(script)) env[name] = 'success';
  // 新 check 即使在当前脚本里还不存在，也显式提供（实现后应被消费）。
  for (const name of EVENT_SCOPED) env[name] = 'success';
  env.EVENT_NAME = scenario.event ?? 'pull_request';
  env.PR_FROM_FORK = scenario.fork ? 'true' : 'false';
  for (const [name, value] of Object.entries(scenario.results ?? {})) env[name] = value;

  const run = spawnSync('bash', ['-c', script], { cwd: ROOT, env, encoding: 'utf8' });
  return { status: run.status, stdout: run.stdout ?? '', stderr: run.stderr ?? '' };
}

const sameRepo = (results) => ({ event: 'pull_request', fork: false, results });
const forkPr = (results) => ({ event: 'pull_request', fork: true, results });
const push = (results) => ({ event: 'push', fork: false, results });

// ---------------------------------------------------------------------------
// release.if 的**目标明确**子句翻译器（不认识子句 → 抛错 fail-closed）
// ---------------------------------------------------------------------------

function splitTopLevel(expr, separator) {
  const parts = [];
  let depth = 0;
  let current = '';
  for (let i = 0; i < expr.length; i += 1) {
    const ch = expr[i];
    if (ch === '(') depth += 1;
    if (ch === ')') depth -= 1;
    if (depth === 0 && expr.startsWith(separator, i)) {
      parts.push(current);
      current = '';
      i += separator.length - 1;
      continue;
    }
    current += ch;
  }
  parts.push(current);
  return parts;
}

function evaluateClause(clause, results, outputs) {
  if (clause === '' || clause === 'always()') return true;

  if (clause.startsWith('(') && clause.endsWith(')')) {
    const inner = clause.slice(1, -1);
    const alternatives = splitTopLevel(inner, '||');
    if (alternatives.length > 1) {
      return alternatives.some((alt) => evaluateClause(alt.trim(), results, outputs));
    }
    return evaluateClause(inner.trim(), results, outputs);
  }

  let match = /^needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*==\s*'([^']*)'$/.exec(clause);
  if (match) return String(outputs[match[1]]?.[match[2]] ?? '') === match[3];

  match = /^!contains\(needs\.\*\.result,\s*'([^']*)'\)$/.exec(clause);
  if (match) return !Object.values(results).includes(match[1]);

  match = /^contains\(needs\.\*\.result,\s*'([^']*)'\)$/.exec(clause);
  if (match) return Object.values(results).includes(match[1]);

  match = /^needs\.([A-Za-z0-9_-]+)\.result\s*==\s*'([^']*)'$/.exec(clause);
  if (match) return results[match[1]] === match[2];

  match = /^needs\.([A-Za-z0-9_-]+)\.result\s*!=\s*'([^']*)'$/.exec(clause);
  if (match) return results[match[1]] !== match[2];

  throw new Error(`无法识别的 release 条件子句：${clause}`);
}

/**
 * 用**真实的 release.if**（从 ci.yml 抽出）判定某个场景下会不会发版。
 * `needs.*.result` 只覆盖 `release.needs` 里的 job（与 GitHub 语义一致）。
 */
export function releaseWouldRun(ciText, { overrides = {}, isRelease = 'true' } = {}) {
  const needs = jobNeeds(ciText, 'release');
  const results = {};
  for (const job of needs) results[job] = 'success';
  for (const [job, state] of Object.entries(overrides)) {
    if (job in results) results[job] = state;
  }
  const outputs = { 'resolve-version': { is_release: isRelease } };
  return splitTopLevel(jobIfExpression(ciText, 'release'), '&&')
    .map((part) => part.trim())
    .filter((part) => part !== '')
    .every((part) => evaluateClause(part, results, outputs));
}

// ---------------------------------------------------------------------------
// 用例：gate 侧（RED = 设计未落地的部分）
// ---------------------------------------------------------------------------

test('E · gate 脚本必须真的消费事件作用域的新 check（否则下面的“预期 skip”用例会假绿）', () => {
  const script = extractGateScript();
  const missing = EVENT_SCOPED.filter((name) => !script.includes(name));
  assert.deepEqual(
    missing,
    [],
    'gate 的判定脚本没有读取这些变量，说明新 check / 事件上下文没有被必需聚合门禁消费：\n  '
      + missing.join('\n  '),
  );
});

test('E · (a) gate.needs 必须包含 pr-metadata 与 codeql', () => {
  const needs = jobNeeds(readCi(), 'gate');
  const missing = ['pr-metadata', 'codeql'].filter((job) => !needs.includes(job));
  assert.deepEqual(
    missing,
    [],
    'gate.needs 里缺少这些 job：它们的 result 不会被门禁看到，失败也不挡合并：\n  '
      + missing.join('\n  '),
  );
});

test('E · (b) gate job 必须声明 R_PR_METADATA / R_CODEQL', () => {
  const keys = jobEnvKeys(readCi(), 'gate');
  const missing = EVENT_SCOPED.filter((key) => !keys.has(key));
  assert.deepEqual(
    missing,
    [],
    'gate job 的 env 里缺少这些结果变量，失败不会被必需聚合门禁看到：\n  '
      + missing.join('\n  '),
  );
});

test('E · pull_request 且 pr-metadata 失败时 CI Gate 必须非 success', () => {
  const { status, stdout } = runGate(sameRepo({ R_PR_METADATA: 'failure' }));
  assert.notEqual(status, 0, `CI Gate 没有因为 pr-metadata=failure 失败：\n${stdout}`);
});

test('E · fork PR 上 pr-metadata 失败同样必须挡住 CI Gate（该 job 在 fork PR 上会运行）', () => {
  const { status, stdout } = runGate(forkPr({ R_PR_METADATA: 'failure' }));
  assert.notEqual(status, 0, `CI Gate 没有因为 fork PR 的 pr-metadata=failure 失败：\n${stdout}`);
});

test('E · 同仓 PR 上 codeql failure / cancelled / skipped 都必须挡住 CI Gate', () => {
  for (const result of ['failure', 'cancelled', 'skipped']) {
    const { status, stdout } = runGate(sameRepo({ R_CODEQL: result }));
    assert.notEqual(
      status,
      0,
      `同仓 PR 上 codeql=${result} 没有挡住 CI Gate（真实失效不得放行）：\n${stdout}`,
    );
  }
});

test('E · fork PR 上 codeql 也必须成功（设计选定 upload: never，job 不再整个跳过）', () => {
  // round5 在 M-9 的两条形态里选定「fork 上跑分析但 upload: never」：
  // 既然 job 会运行，它的 failure/cancelled/skipped 都必须挡住门禁，不存在“预期 skip”。
  for (const result of ['failure', 'cancelled', 'skipped']) {
    const { status, stdout } = runGate(forkPr({ R_CODEQL: result }));
    assert.notEqual(
      status,
      0,
      `fork PR 上 codeql=${result} 没有挡住 CI Gate（选定 upload: never 后 codeql 必须在 fork PR 上运行）：\n${stdout}`,
    );
  }
});

test('E · push 到 master 上 codeql 失败必须挡住 CI Gate', () => {
  const { status, stdout } = runGate(push({ R_CODEQL: 'failure', R_PR_METADATA: 'success' }));
  assert.notEqual(status, 0, `push 上 codeql=failure 没有挡住 CI Gate：\n${stdout}`);
});

test('E · push 上 pr-metadata 的 failure / cancelled / skipped / missing 都必须挡住', () => {
  for (const result of ['failure', 'cancelled', 'skipped', '']) {
    const { status, stdout } = runGate(push({ R_PR_METADATA: result, R_CODEQL: 'success' }));
    assert.notEqual(
      status,
      0,
      `push 上 pr-metadata=${result || '<missing>'} 被放行（不登记预期 skip）：\n${stdout}`,
    );
  }
});

test('E · push 上 pr-metadata=success（无 body 的步骤自身 exit 0）且其余 success 时放行', () => {
  const { status, stdout } = runGate(push({ R_PR_METADATA: 'success', R_CODEQL: 'success' }));
  assert.equal(status, 0, `push 全绿仍被挡住：\n${stdout}`);
});

test('E · 全绿场景必须通过（证明上面失败不是脚本/环境问题）', () => {
  const { status, stdout } = runGate(sameRepo({}));
  assert.equal(status, 0, `所有 result 均为 success 时 CI Gate 仍然失败（脚本执行或环境有问题）：\n${stdout}`);
});

test('E · 既有必需 job 的失败语义保持不变（回归锁）', () => {
  const { status, stdout } = runGate(sameRepo({ R_TEST_CORE: 'failure' }));
  assert.notEqual(status, 0, `test-client-core=failure 没有挡住 CI Gate：\n${stdout}`);
});

// ---------------------------------------------------------------------------
// 用例：codeql job 结构（RED）
// ---------------------------------------------------------------------------

test('E · codeql 必须在 fork PR 上运行（去掉整 job 跳过），并按事件给 upload 取值', () => {
  const ci = readCi();
  const lines = ci.split(/\r?\n/);
  const start = lines.findIndex((line) => /^ {2}codeql:\s*$/.test(line));
  assert.ok(start >= 0, 'ci.yml 没有 codeql job');
  const own = [];
  for (let i = start + 1; i < lines.length; i += 1) {
    if (/^ {2}[A-Za-z0-9_-]+:\s*$/.test(lines[i])) break;
    own.push(lines[i]);
  }
  const jobIf = own.filter((line) => /^ {4}if:/.test(line)).join(' ');
  assert.ok(
    !/head\.repo\.full_name\s*==\s*github\.repository/.test(jobIf),
    `codeql.if 仍然在 fork PR 上跳过整个 job（当前 if = ${jobIf}）：`
      + 'fork 上要跑分析，upload 用 never，job 不能整段跳过',
  );

  const block = (jobBlockLines(ci, 'codeql') ?? []).join('\n');
  const upload = /^\s+upload:\s*(.+?)\s*$/m.exec(block);
  assert.ok(upload, 'codeql 的 analyze 步骤没有设置 upload 输入（fork PR 上会尝试上传 SARIF 而 403）');
  const value = upload[1];
  assert.match(
    value,
    /head\.repo\.full_name|EVENT_NAME|PR_FROM_FORK/,
    `upload 的取值没有按事件区分：${value}。期望：同仓 PR / push / schedule → always，fork PR → never`,
  );
  assert.match(value, /never/, `upload 的取值里没有 fork 分支的 'never'：${value}`);
  assert.match(value, /always/, `upload 的取值里没有同仓/主干的 'always'：${value}`);
});

test('E · 不得改用 pull_request_target，也不得依赖 send-write-tokens 设置', () => {
  const ci = readCi();
  assert.ok(
    !/pull_request_target/.test(ci),
    'ci.yml 里出现了 pull_request_target：那会给 fork PR 写权限（并依赖仓库设置），本仓不采用',
  );
  // codeql 的触发方式必须仍是 pull_request（配合 upload: never，而不是提权）。
  const onBlock = ci.slice(0, ci.indexOf('permissions:')) ?? '';
  assert.match(onBlock, /pull_request:/, 'ci.yml 的 on: 段里没有 pull_request 触发');
});

// ---------------------------------------------------------------------------
// 用例：release 侧（真实 if 表达式参与判定）
// ---------------------------------------------------------------------------

test('E · release 侧：gate 与 codeql 必须在 release.needs 里；事件条件 job 不得挡住发版', () => {
  const ci = readCi();
  const releaseNeeds = jobNeeds(ci, 'release');
  for (const job of ['gate', 'codeql']) {
    assert.ok(releaseNeeds.includes(job), `release.needs 缺少 ${job}：它的失败不会阻止发版`);
  }

  for (const job of jobNeeds(ci, 'gate')) {
    assert.ok(
      releaseNeeds.includes(job),
      `gate.needs 里的 ${job} 不在 release.needs 里：`
        + '它的 failure/skipped/cancelled 对 release 不可见（审计 F-05）。不得登记预期 skip 豁免。',
    );
  }
  assert.ok(releaseNeeds.includes('pr-metadata'), 'pr-metadata 必须进入 release.needs');
});

test('E · release 侧：master 正常发布，任一必需检查失败都挡住（真实 release.if 判定）', () => {
  const ci = readCi();

  // ① push 到 master、所有 release.needs 均 success → 发版。
  assert.equal(releaseWouldRun(ci), true, '正常场景（push master + 全绿 + is_release）居然不发版');

  // ② pr-metadata 在 push 上必须是 success。skipped / failure / cancelled 都挡住发版。
  for (const state of ['failure', 'cancelled', 'skipped']) {
    assert.equal(
      releaseWouldRun(ci, { overrides: { 'pr-metadata': state } }),
      false,
      `pr-metadata=${state} 没有挡住 master 发版`,
    );
  }

  // ③ codeql 失败 → 不许发版（它在 release.needs 里且现在在所有事件上都会运行）。
  assert.equal(releaseWouldRun(ci, { overrides: { codeql: 'failure' } }), false, 'codeql=failure 没有挡住发版');
  assert.equal(releaseWouldRun(ci, { overrides: { codeql: 'skipped' } }), false, 'codeql=skipped 没有挡住发版');
  assert.equal(releaseWouldRun(ci, { overrides: { codeql: 'cancelled' } }), false, 'codeql=cancelled 没有挡住发版');

  // ④ 聚合门禁失败 → 不许发版。
  assert.equal(releaseWouldRun(ci, { overrides: { gate: 'failure' } }), false, 'gate=failure 没有挡住发版');

  // ⑤ 非发版事件（PR / schedule：is_release=false）→ 不发版。
  assert.equal(releaseWouldRun(ci, { isRelease: 'false' }), false, 'is_release=false 时仍然发版');
});

test('E · release 条件求值必须 fail-closed：出现不认识的子句就抛错，不做近似', () => {
  const withUnknown = readCi().replace(
    "needs.resolve-version.outputs.is_release == 'true' &&",
    "needs.resolve-version.outputs.is_release == 'true' && github.ref == 'refs/heads/master' &&",
  );
  assert.notEqual(withUnknown, readCi(), '探针没有改动 release.if：fail-closed 用例本身失效了');
  assert.throws(
    () => releaseWouldRun(withUnknown),
    /无法识别的 release 条件子句/,
    'release 条件里出现不认识的子句时没有 fail-closed（近似求值会把“不确定”当成“不发版/会发版”）',
  );

  // 正对照：真实 ci.yml 的条件可以被完整翻译（每个子句都认识）。
  assert.doesNotThrow(() => releaseWouldRun(readCi()));
});

// ---------------------------------------------------------------------------
// 用例：pr-metadata job 真实运行步骤（"push metadata job 运行 step 成功"的绑定）
// ---------------------------------------------------------------------------

test('E · pr-metadata job 在全部 event 上运行，push 无 body 时步骤自身 exit 0', () => {
  const ci = readCi();
  const lines = ci.split(/\r?\n/);
  const start = lines.findIndex((line) => /^ {2}pr-metadata:\s*$/.test(line));
  assert.ok(start >= 0, 'ci.yml 没有 pr-metadata job');
  const own = [];
  for (let i = start + 1; i < lines.length; i += 1) {
    if (/^ {2}[A-Za-z0-9_-]+:\s*$/.test(lines[i])) break;
    own.push(lines[i]);
  }
  const ownIf = own.filter((line) => /^ {4}if:/.test(line));
  assert.deepEqual(
    ownIf,
    [],
    `pr-metadata 仍有 job if（${ownIf.join(' | ')}）。job 必须在 push/PR/schedule/workflow_dispatch 上都运行，不能靠 skip 避开无 body。`,
  );
  const block = (jobBlockLines(ci, 'pr-metadata') ?? []).join('\n');
  assert.match(block, /node scripts\/ci\/check-pr-metadata\.mjs/, 'pr-metadata job 没有跑真实校验脚本');
  for (const key of ['EVENT_NAME', 'PR_TITLE', 'PR_BODY', 'HEAD_REF']) {
    assert.ok(block.includes(key), `pr-metadata step 没有传入 ${key}`);
  }

  const script = path.join(ROOT, 'scripts/ci/check-pr-metadata.mjs');
  const run = (env) => spawnSync(process.execPath, [script], { env: { ...process.env, ...env }, encoding: 'utf8' });
  for (const eventName of ['push', 'schedule', 'workflow_dispatch']) {
    const result = run({ EVENT_NAME: eventName, PR_TITLE: '', PR_BODY: '', HEAD_REF: '' });
    assert.equal(result.status, 0, `${eventName} 无 body 时期望步骤 exit 0，实际 ${result.status}\n${result.stdout}\n${result.stderr}`);
  }
  const prEmpty = run({ EVENT_NAME: 'pull_request', PR_TITLE: '', PR_BODY: '', HEAD_REF: '' });
  assert.notEqual(prEmpty.status, 0, 'pull_request 空 body 被放行');
  const prGood = run({
    EVENT_NAME: 'pull_request',
    PR_TITLE: 'fix: 修正 / fix queue',
    PR_BODY: [
      '## 技术修改 / Technical changes',
      '## 功能变化 / Feature changes',
      '## 如何体验 / How to try it',
      '## 测试 / Tests',
    ].join('\n'),
    HEAD_REF: 'grok-linux/audit-review',
  });
  assert.equal(prGood.status, 0, `合规 PR 未通过：\n${prGood.stderr}`);
});

test('E · pr-metadata job 的执行步骤与既有行为测试保持绑定', () => {
  const ci = readCi();
  const block = (jobBlockLines(ci, 'pr-metadata') ?? []).join('\n');
  assert.match(block, /node scripts\/ci\/check-pr-metadata\.mjs/, 'pr-metadata job 没有跑真实校验脚本');
  for (const key of ['PR_TITLE', 'PR_BODY', 'HEAD_REF']) {
    assert.ok(block.includes(key), `pr-metadata job 没有把 ${key} 传给校验脚本（会校验到空 payload 而假绿）`);
  }
  assert.ok(
    fs.existsSync(path.join(ROOT, 'scripts/ci/test-pr-metadata.mjs')),
    '缺少 scripts/ci/test-pr-metadata.mjs：校验脚本没有行为测试',
  );
  // 该脚本的用例由 ci-scripts 的 `node --test scripts/ci/test-*.mjs` 真实执行。
  assert.ok(
    (readCi().match(/node --test scripts\/ci\/test-\*\.mjs/) ?? []).length > 0,
    'ci-scripts 没有收集 scripts/ci/test-*.mjs（pr-metadata 的行为测试不会被执行）',
  );
});

// ---------------------------------------------------------------------------
// F-11 · 审计守卫的排除集合必须为空（round7 关闭 Grok round6 Minor W-1）
//
// round3…round6 期间 `ci-scripts` 用 `--exclude webview-token-policy` 临时隔离 F-11；
// 该临时项已在 F-11 实施后删除。这条断言把「不再有第二个 exclude」变成机械约束：
// 以后谁再加一个 `--exclude`（哪怕是为了让 CI 变绿），这里立刻红。
// ---------------------------------------------------------------------------
test('E · ci-scripts 的审计守卫不得使用 --exclude（F-11 落地后排除集合必须为空）', () => {
  const ci = readCi();
  const block = (jobBlockLines(ci, 'ci-scripts') ?? []).join('\n');
  assert.ok(block.length > 0, 'ci.yml 里找不到 ci-scripts job');

  const runLines = block
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.startsWith('run:'));

  assert.ok(
    runLines.some((line) => line.includes('node scripts/ci/check-audit-guards.mjs')),
    'ci-scripts 没有直接执行 node scripts/ci/check-audit-guards.mjs',
  );
  for (const line of runLines) {
    assert.ok(
      !line.includes('--exclude'),
      `审计守卫仍然带 --exclude（排除掉的门禁等于没有门禁）：${line}`,
    );
  }
});
