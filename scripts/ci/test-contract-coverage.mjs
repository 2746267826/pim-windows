#!/usr/bin/env node
/**
 * REQ-COM-02 / AC-COM-02 · 审计 **F-03** 的「分母与覆盖」检查（round4 建立，round5 加固）。
 *
 * 审计 F-03 的原话是「本仓对契约零机械约束」；round2/round3 补上了**字段层**的机械约束
 * （`ContractWireShapeTests` 直接读 `contract/openapi.json`），round4 建立了「分母 + 覆盖」
 * 检查，round5 按独立 review（`reviews/windows-round4-deepseek.md` M-3/M-4/M-5）加固：
 *
 *   ① **分母完整性**：从 `src/client-windows` + `src/client-shell-windows` 里机械枚举
 *      **全部**生产 HTTP operation（method + 绝对路径），每条都要能在契约里查到。
 *   ② **覆盖（call 级）**：每个生产 operation 都要有一个**参与编译**的契约用例**真的调用**
 *      `ContractOperation.ForPath("<path>", "<method>")` / `ForOperation(...)`。
 *      round4 的判据是「文件里同时出现路径字面量和 `ContractOperation` 字符串」——
 *      注释里写一行就能骗过（M-3）。round5 改为**调用级**并先剥注释。
 *   ③ round5 还修掉两个真实的漏检（M-4）：
 *      - 调用可以**跨行**（方法名、泛型参数、左括号、字面量分行也能枚举到）；
 *      - **非泛型**调用（`PostAsync("/x", ...)`）也要枚举到。
 *      另外 `const string LegacyUploadPath = "..."` 这类**常量间接**在覆盖判定里会被解引用，
 *      所以 6 个已有 operation 不会因为「字面量写在常量里」而被误判为未覆盖。
 *   ④ **排除项必须真实**（M-5）：本文件不是 PIM API 的本地端点在枚举后**逐条打印**，
 *      并在用例里断言清单内容（round4 的 `EXCLUDED_LOCAL_PATHS` 是死路径、输出为空）。
 *
 * 反控制（anti-gaming，全部在用例里真实执行）：
 *   - 在临时目录里合成 `src/client-windows/*.cs`，验证跨行/非泛型/动态路径/注释四种形态
 *     的枚举结果（证明检查不是「扫不到东西所以绿」）；
 *   - 合成一个只把调用**写在注释里**的测试工程，验证覆盖判定为 0（证明注释骗不过去）；
 *   - 合成缺口必须被 `uncoveredOperations` 识别；真实枚举器必须认得已覆盖的 operation。
 *
 * 用法：`node --test scripts/ci/test-contract-coverage.mjs`
 * （已由既有 `ci-scripts` job 的 `node --test scripts/ci/test-*.mjs` 收集，无需新 job。）
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');

const SRC_DIRS = ['src/client-windows', 'src/client-shell-windows'];

/**
 * 参与编译的测试工程。第三项是壳侧工程（net8.0 目标以 Link 方式编译
 * `tests/ClientWindows/ContractSchema.cs` 与 `ShellHealthContractWireTests.cs`），
 * 它的 `dotnet test` 在 `build-shell-windows.yml` 里真实执行。
 */
const TEST_PROJECTS = [
  'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
  'tests/Pim.Client.App.Tests/Pim.Client.App.Tests.csproj',
  'src/client-shell-windows/Pim.Shell.Tests/Pim.Shell.Tests.csproj',
];

/** 直连 HttpClient 的 PIM 端点（不带 /api/v1 前缀）的字面量后缀。 */
const DIRECT_PATHS = ['/health', '/api/client/shell/latest'];

/** 审计确认「不是 PIM API」的本地端点前缀，明确排除而不是漏掉（枚举后逐条打印）。 */
const EXCLUDED_LOCAL_PREFIXES = [
  { prefix: '/api/stats/', why: '本地 KeyStats 统计服务（KeyStatsBaseUrl），不走 ApiClient、不在契约里' },
  { prefix: '/browser/', why: '浏览器桥（BrowserBridgeService，本机 127.0.0.1），不走 ApiClient、不在契约里' },
];

/** 会发起 HTTP 的方法名（`ApiClient` 家族 + 转发 + 直连 HttpClient 的读/写）。 */
const CALL_NAMES = [
  'PostWithoutTokenRenewalAsync',
  'PostAsync',
  'GetAsync',
  'PutAsync',
  'DeleteAsync',
  'TryPostAsync',
  'GetFromJsonAsync',
  'PostAsJsonAsync',
].join('|');

/**
 * 一次调用：`Method<泛型>( "字面量"` / `Method( "字面量"` / `Method($"…")` / `Method(new Uri($"…")`。
 * `\s*` 覆盖跨行写法；泛型参数可选（非泛型方法）。
 */
const CALL = new RegExp(
  String.raw`\b(` + CALL_NAMES + String.raw`)\s*(?:<[^;{}()]*?>)?\s*\(\s*(?:new\s+Uri\s*\(\s*)?\$?\s*"([^"\n]*)"`,
  'g',
);

/** 覆盖判据：真实调用 `ContractOperation.ForPath|ForOperation(<字面量|常量>, "<method>")`。 */
const COVERAGE_CALL = /ContractOperation\s*\.\s*(?:ForPath|ForOperation)\s*\(\s*(?:"([^"]+)"|([A-Za-z_]\w*))\s*,\s*"([^"]+)"\s*\)/g;

/** 同文件里的 `const string X = "…"`，用于把覆盖判据里的常量解引用（不是编译器，只认字面量）。 */
const CONST_STRING = /(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"([^"]*)"/g;

/** 任意字符串字面量（用于“裸字面量”兜底扫描：`"/health"` 这类拼接形态）。 */
const STRING_LITERAL = /"([^"\n]*)"/g;

/**
 * 把 `#if false` / `#if FALSE` 条件编译块整体置空（保留换行与长度）。
 *
 * round5 独立审查 M-1：扫描器原先只看"剥注释后的文本里出现过调用"，于是
 * `#if false` 里永远编译不进去的 `PostAsync("/should-not-count")` 也会被算成
 * 契约覆盖（生产分母与测试覆盖两侧都会被污染）。这里做的是**最小词法防护**，
 * 不是完整 C# 预处理器：
 *   - 只识别字面量 `false`（`#if (false)` 也算）作为禁用条件；
 *   - 块内按 `#if` 计数嵌套；
 *   - 同层遇到 `#elif` / `#else` 即认为后续分支重新参与编译（宁可多算，不可漏算）。
 * 判断偏保守：多剥掉只会让覆盖面变小（红），不会把禁用的调用算成绿。
 */
export function blankDisabledBlocks(text) {
  const lines = text.split('\n');
  let depth = 0; // > 0 表示当前处于被禁用分支内
  const out = lines.map((line) => {
    const dir = /^\s*#\s*(if|elif|else|endif)\b(.*)$/.exec(line);
    if (depth === 0) {
      const disables = dir && dir[1] === 'if' && /^\s*\(?\s*false\s*\)?\s*$/.test(dir[2]);
      if (!disables) return line;
      depth = 1;
      return ' '.repeat(line.length);
    }
    if (dir) {
      if (dir[1] === 'if') depth += 1;
      else if (dir[1] === 'endif') {
        depth -= 1;
        if (depth === 0) return ' '.repeat(line.length);
      } else if (depth === 1) {
        // #elif / #else：同层的另一个分支，重新按可编译代码看待。
        depth = 0;
        return line;
      }
    }
    return ' '.repeat(line.length);
  });
  return out.join('\n');
}

/**
 * 去掉 C# 注释，但**保持长度不变**（注释字符替换为空格），这样 match.index → 行号仍然准确。
 * 需要处理字符串/字符字面量里的 `//`（例如 "http://…"）与转义，因此是一个小的状态机，
 * 不是"把 // 之后全删掉"的正则 —— 后者会把 URL 字面量截断。
 *
 * round5 独立审查 M-1：`"""`（C# 原始字符串）里的调用以前会被算成真实调用，
 * 这里按原样剥掉整段原始字符串（开闭引号数量对齐，`""""` 也支持），只做词法层面的事。
 */
export function stripComments(text) {
  const src = blankDisabledBlocks(text);
  const out = src.split('');
  let i = 0;
  let state = 'code'; // code | line | block | string | char | verbatim | raw
  let rawQuotes = 0; // 原始字符串开引号的个数
  while (i < src.length) {
    const ch = src[i];
    const next = src[i + 1];
    if (state === 'code') {
      if (ch === '"' && next === '"' && src[i + 2] === '"') {
        let n = 0;
        while (src[i + n] === '"') n += 1;
        rawQuotes = n;
        for (let k = 0; k < n; k += 1) out[i + k] = ' ';
        state = 'raw';
        i += n;
        continue;
      }
      if (ch === '/' && next === '/') {
        state = 'line';
        out[i] = ' ';
        out[i + 1] = ' ';
        i += 2;
        continue;
      }
      if (ch === '/' && next === '*') {
        state = 'block';
        out[i] = ' ';
        out[i + 1] = ' ';
        i += 2;
        continue;
      }
      if (ch === '"') state = 'string';
      else if (ch === "'") state = 'char';
      else if (ch === '@' && next === '"') {
        state = 'verbatim';
        i += 2;
        continue;
      }
      i += 1;
      continue;
    }
    if (state === 'line') {
      if (ch === '\n') state = 'code';
      else out[i] = ' ';
      i += 1;
      continue;
    }
    if (state === 'block') {
      if (ch === '*' && next === '/') {
        out[i] = ' ';
        out[i + 1] = ' ';
        state = 'code';
        i += 2;
        continue;
      }
      if (ch !== '\n') out[i] = ' ';
      i += 1;
      continue;
    }
    if (state === 'verbatim') {
      if (ch === '"' && next === '"') {
        i += 2;
        continue;
      }
      if (ch === '"') state = 'code';
      i += 1;
      continue;
    }
    if (state === 'raw') {
      if (ch === '"') {
        let n = 0;
        while (src[i + n] === '"') n += 1;
        for (let k = 0; k < n; k += 1) out[i + k] = ' ';
        if (n >= rawQuotes) state = 'code';
        i += n;
        continue;
      }
      if (ch !== '\n') out[i] = ' ';
      i += 1;
      continue;
    }
    // string / char
    if (ch === '\\') {
      i += 2;
      continue;
    }
    if ((state === 'string' && ch === '"') || (state === 'char' && ch === "'")) state = 'code';
    i += 1;
  }
  return out.join('');
}

function walkCsharpFiles(root, relDir) {
  const absDir = path.join(root, relDir);
  if (!fs.existsSync(absDir)) return [];
  const out = [];
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (['bin', 'obj', 'node_modules', '.git'].includes(entry.name)) continue;
        walk(path.join(dir, entry.name));
      } else if (entry.name.endsWith('.cs')) {
        out.push(path.join(dir, entry.name));
      }
    }
  };
  walk(absDir);
  return out.sort();
}

const joinApiV1 = (raw) => `/api/v1/${raw.replace(/^\/+/, '')}`;
const asAbsolute = (p) => (p.startsWith('/') ? p : `/${p}`);
const keyOf = (method, p) => `${method.toLowerCase()} ${p}`;

/**
 * 把一次调用的字面量归类成 operation / 排除项。
 *
 * 判「直连 PIM 端点」（不带 /api/v1 前缀）的规则必须同时看**字面量形态**：
 *   - 完全等于 `/health`（StatusWindow 的裸字面量）；
 *   - 或者以 `/health` 结尾**且**是 URL 模板/绝对 URL（`{base}/health`、`https://x/health`）。
 * 只按 `endsWith` 会把 `ApiClient` 的相对路径 `/pc/tracker/health` 误判成 `get /health`
 * （round5 修掉的就是这个：它会让分母出现一个不存在的 operation）。
 */
function classifyLiteral(raw) {
  const direct = DIRECT_PATHS.find(
    (p) => raw === p || (raw.endsWith(p) && (raw.includes('{') || /^https?:\/\//.test(raw))),
  );
  // 直连端点的路径是固定字面量后缀，不算「动态契约路径」（动态检查针对 /api/v1/ 下的插值路径）。
  if (direct) return { kind: 'pim', path: direct, dynamic: false };
  if (raw.startsWith('/api/v1/')) return { kind: 'pim', path: raw, dynamic: raw.includes('{') };
  const excluded = EXCLUDED_LOCAL_PREFIXES.find((e) => raw.startsWith(e.prefix) || raw.includes(e.prefix));
  if (excluded) return { kind: 'excluded', ...excluded };
  return { kind: 'pim', path: joinApiV1(raw), dynamic: raw.includes('{') };
}

/** HTTP 方法名 → operation 的 method（`TryPostAsync` 也发 POST，不能用 /^Post/ 判）。 */
function methodOf(name) {
  if (name === 'GetAsync' || name === 'GetFromJsonAsync') return 'get';
  if (name === 'PutAsync') return 'put';
  if (name === 'DeleteAsync') return 'delete';
  return 'post';
}

/**
 * 机械枚举生产 operation。
 * @returns {{operations: Map<string, {method: string, path: string, sites: string[], dynamic: boolean}>,
 *            excluded: Array<{site: string, raw: string, prefix: string, why: string}>}}
 */
export function enumerateProductionOperations(root = ROOT) {
  const operations = new Map();
  const excluded = [];

  const add = (method, p, site, dynamic = false) => {
    const abs = asAbsolute(p);
    const key = keyOf(method, abs);
    if (!operations.has(key)) operations.set(key, { method: method.toLowerCase(), path: abs, sites: [], dynamic });
    const entry = operations.get(key);
    if (!entry.sites.includes(site)) entry.sites.push(site);
    entry.dynamic = entry.dynamic || dynamic;
  };

  const addExcluded = (site, raw, hit) => {
    if (!excluded.some((e) => e.site === site && e.prefix === hit.prefix)) {
      excluded.push({ site, raw, prefix: hit.prefix, why: hit.why });
    }
  };

  for (const relDir of SRC_DIRS) {
    for (const file of walkCsharpFiles(root, relDir)) {
      const rel = path.relative(root, file);
      const rawText = fs.readFileSync(file, 'utf8');
      const text = stripComments(rawText);
      const lineOf = (index) => rel + ':' + (text.slice(0, index).split('\n').length);

      // ① 真实调用形态（跨行 / 泛型 / 非泛型 / new Uri($"…")）。
      const consumed = [];
      for (const match of text.matchAll(CALL)) {
        const method = methodOf(match[1]);
        const raw = match[2];
        consumed.push([match.index, match.index + match[0].length]);
        const site = lineOf(match.index);
        const hit = classifyLiteral(raw);
        if (hit.kind === 'excluded') addExcluded(site, raw, hit);
        else add(method, hit.path, site, hit.dynamic);
      }

      // ② 裸字面量兜底：`DaemonConfig.Load().ServerUrl.TrimEnd('/') + "/health"` 这类
      //    拼接形态（字面量不是任何 HTTP 调用的第一个实参）。只认 PIM 直连端点；
      //    桥的 `/browser/*` 是 HttpListener 的**服务端路由**（`req.Url?.AbsolutePath == "…"`），
      //    不是客户端调用，因此不该被枚举成生产 operation 或排除项。
      for (const literal of text.matchAll(STRING_LITERAL)) {
        const start = literal.index;
        const end = start + literal[0].length;
        if (consumed.some(([a, b]) => start >= a && end <= b)) continue;
        const raw = literal[1];
        const direct = DIRECT_PATHS.find((p) => raw === p || raw.endsWith(p));
        if (direct) add('get', direct, lineOf(start));
      }
    }
  }

  for (const entry of operations.values()) entry.sites.sort();
  excluded.sort((a, b) => (a.site < b.site ? -1 : a.site > b.site ? 1 : 0));
  return { operations, excluded };
}

/** csproj 里的 `Compile Include` → 磁盘上的真实文件（分隔符按 / 归一，兼容 Windows 写法）。 */
export function compiledTestFiles(root = ROOT) {
  const files = [];
  for (const relProject of TEST_PROJECTS) {
    const absProject = path.join(root, relProject);
    if (!fs.existsSync(absProject)) continue;
    const text = fs.readFileSync(absProject, 'utf8');
    for (const match of text.matchAll(/Compile Include="([^"]+)"/g)) {
      const normalized = match[1].split('\\').join('/');
      const abs = path.normalize(path.join(path.dirname(absProject), normalized));
      if (fs.existsSync(abs)) files.push(abs);
    }
  }
  return [...new Set(files)].sort();
}

/**
 * 覆盖判定（call 级）：编译中的测试文件里必须**真的调用**
 * `ContractOperation.ForPath|ForOperation("<path>", "<method>")`（先剥注释；
 * 路径写在同文件 `const string` 里时解引用）。它证明「有编译中的契约用例引用该 operation」，
 * 不证明每个生产调用点都被驱动（见报告口径）。
 */
export function enumerateContractCoverage(root = ROOT) {
  const covered = new Set();
  for (const file of compiledTestFiles(root)) {
    const text = stripComments(fs.readFileSync(file, 'utf8'));
    const consts = new Map();
    for (const match of text.matchAll(CONST_STRING)) consts.set(match[1], match[2]);
    for (const match of text.matchAll(COVERAGE_CALL)) {
      const literal = match[1] ?? consts.get(match[2]);
      if (!literal) continue;
      covered.add(keyOf(match[3], literal));
    }
  }
  return covered;
}

export function uncoveredOperations(denominatorKeys, covered) {
  return [...denominatorKeys].filter((key) => !covered.has(key)).sort();
}

/** 人可读的枚举摘要（真实打印，便于在 CI 日志里核对分母与排除项）。 */
export function formatSummary(root = ROOT) {
  const { operations, excluded } = enumerateProductionOperations(root);
  const lines = [];

  lines.push(`[F-03] 生产 operation 分母：${operations.size} 条`);
  for (const [key, op] of [...operations].sort()) {
    lines.push(`[F-03]   分母 ${key}  ← ${op.sites.join(', ')}${op.dynamic ? '（动态路径）' : ''}`);
  }

  lines.push(`[F-03] 排除的本地非 PIM 端点：${excluded.length} 条`);
  for (const e of excluded) {
    lines.push(`[F-03]   排除 ${e.prefix}（${e.why}）← ${e.site} 字面量=${JSON.stringify(e.raw)}`);
  }

  const covered = enumerateContractCoverage(root);
  const missing = uncoveredOperations(operations.keys(), covered);
  lines.push(`[F-03] 编译中契约用例覆盖：${operations.size - missing.length}/${operations.size}`);
  for (const key of missing) lines.push(`[F-03]   缺口 ${key}`);
  lines.push(`[F-03] 参与编译的测试文件：${compiledTestFiles(root).length} 个（${TEST_PROJECTS.length} 个工程）`);
  return lines.join('\n');
}

function contract() {
  return JSON.parse(fs.readFileSync(path.join(ROOT, 'contract/openapi.json'), 'utf8'));
}

/** 合成 tree 的辅助：在临时目录里造 src/client-windows 下的 .cs 文件。 */
function syntheticRoot(files) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pim-f03-'));
  fs.mkdirSync(path.join(root, SRC_DIRS[0]), { recursive: true });
  for (const [name, content] of Object.entries(files)) {
    fs.writeFileSync(path.join(root, SRC_DIRS[0], name), content, 'utf8');
  }
  return root;
}

function makeSyntheticProject(root, { project, files }) {
  const dir = path.join(root, path.dirname(project));
  fs.mkdirSync(dir, { recursive: true });
  const items = Object.keys(files).map((f) => `    <Compile Include="${f}" />`).join('\n');
  fs.writeFileSync(
    path.join(root, project),
    `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${items}\n  </ItemGroup>\n</Project>\n`,
    'utf8',
  );
  for (const [name, content] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), content, 'utf8');
  }
}

// ---------------------------------------------------------------------------
// 用例
// ---------------------------------------------------------------------------

test('F-03 分母：生产 operation 全部能在契约里查到对应 method', () => {
  const { operations } = enumerateProductionOperations();
  const spec = contract();
  const missing = [];
  for (const op of operations.values()) {
    const item = spec.paths?.[op.path];
    if (!item) {
      missing.push(`${op.method.toUpperCase()} ${op.path}（契约里没有这个 path）← ${op.sites.join(', ')}`);
    } else if (!item[op.method]) {
      missing.push(`${op.method.toUpperCase()} ${op.path}（契约有 path 但没有 ${op.method}）← ${op.sites.join(', ')}`);
    }
  }
  assert.deepEqual(missing, [], `生产调用了契约里不存在的 operation：\n  ${missing.join('\n  ')}`);
});

test('F-03 分母不是空集（避免检查因为扫不到东西而空转）', () => {
  const { operations } = enumerateProductionOperations();
  assert.equal(operations.size, 11, `生产 operation 分母应当是 11 条，实际 ${operations.size} 条`);

  // 反向哨兵：枚举器必须真的看得见已知的各种调用形态。
  const keys = new Set(operations.keys());
  for (const known of [
    'post /api/v1/daemon/heartbeat',
    'get /health',
    'get /api/client/shell/latest',
    'post /api/v1/auth/login',
    'post /api/v1/pc/tracker/health',
  ]) {
    assert.ok(keys.has(known), `没有枚举到已知的生产 operation：${known}`);
  }
});

test('F-03 枚举器：跨行 / 非泛型 / 动态路径 / 注释 四种形态都真实处理（M-4 反控制）', () => {
  const root = syntheticRoot({
    'MultiLine.cs': [
      'class A {',
      '  async Task B() {',
      '    await _api.PostAsync<ApiResponse<string>>(',
      '        "/pc/tracker/health", req, ct);',
      '  }',
      '}',
    ].join('\n'),
    'NonGeneric.cs': 'class C { void D() { _http.PostAsync("/plain/non-generic", body); } }',
    'Dynamic.cs': 'class E { void F(string id) { _api.GetAsync<X>($"/pc/endpoints/{id}/state"); } }',
    'Commented.cs': 'class G { // _api.PostAsync<X>("/commented/out", body);\n  void H() { } }',
  });

  try {
    const { operations } = enumerateProductionOperations(root);
    const keys = [...operations.keys()].sort();
    assert.deepEqual(keys, [
      'get /api/v1/pc/endpoints/{id}/state',
      'post /api/v1/pc/tracker/health',
      'post /api/v1/plain/non-generic',
    ], `枚举结果与预期不符：${JSON.stringify(keys)}`);

    assert.equal(operations.get('post /api/v1/pc/tracker/health').dynamic, false);
    assert.equal(operations.get('get /api/v1/pc/endpoints/{id}/state').dynamic, true);
    assert.ok(
      !keys.some((k) => k.includes('commented')),
      '注释里的调用被枚举成了生产 operation（注释必须先被剥掉）',
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('F-03 枚举器：`#if false` 与 C# 原始字符串里的调用不得算成真实调用（M-1 反控制）', () => {
  // round5 独立审查 M-1：这两种形态以前会被算成契约覆盖（编译不进去 / 只是字符串）。
  // 反控制必须是双向的：禁用块与原始字符串**不算**，而同一文件里可编译的调用**仍要算**，
  // 否则「一刀切多剥」也能骗过这条用例。
  const root = syntheticRoot({
    'Disabled.cs': [
      'class A {',
      '  void Live() { _api.PostAsync<X>("/live/kept", body); }',
      '#if false',
      '  void Dead() { _api.PostAsync<X>("/disabled/false", body); }',
      '#endif',
      '}',
    ].join('\n'),
    'DisabledElse.cs': [
      'class B {',
      '#if false',
      '  void Dead() { _api.PostAsync<X>("/disabled/branch", body); }',
      '#else',
      '  void Live() { _api.PostAsync<X>("/else/live", body); }',
      '#endif',
      '}',
    ].join('\n'),
    'RawString.cs': [
      'class C {',
      '  string Sample = """',
      '    _api.PostAsync<X>("/raw-string/sample", body);',
      '    """;',
      '  void Live() { _api.PostAsync<X>("/raw/live", body); }',
      '}',
    ].join('\n'),
  });

  try {
    const { operations } = enumerateProductionOperations(root);
    const keys = [...operations.keys()].sort();
    assert.deepEqual(keys, [
      'post /api/v1/else/live',
      'post /api/v1/live/kept',
      'post /api/v1/raw/live',
    ], `枚举结果与预期不符：${JSON.stringify(keys)}`);
    for (const ghost of ['disabled/false', 'disabled/branch', 'raw-string/sample']) {
      assert.ok(
        !keys.some((k) => k.includes(ghost)),
        `${ghost} 被算成了真实生产 operation（编译不进去的代码 / 字符串内容不算覆盖）`,
      );
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('F-03 原始字符串剥离不破坏长度守恒（行号必须仍然准确）', () => {
  const text = [
    'class A {',
    '  string S = """',
    '    _api.PostAsync<X>("/inside", body);',
    '    """;',
    '  void B() { _api.PostAsync<X>("/outside", body); }',
    '#if false',
    '  void C() { _api.PostAsync<X>("/off", body); }',
    '#endif',
    '}',
  ].join('\n');
  const stripped = stripComments(text);
  assert.equal(stripped.length, text.length, '剥注释/原始字符串后长度变了，行号映射会失效');
  assert.equal(
    stripped.split('\n').length,
    text.split('\n').length,
    '剥注释/原始字符串后行数变了',
  );
  assert.ok(stripped.includes('"/outside"'), '可编译的真实调用被误剥掉了');
  assert.ok(!stripped.includes('"/inside"'), '原始字符串里的调用没有被剥掉');
  assert.ok(!stripped.includes('"/off"'), '`#if false` 块里的调用没有被剥掉');
});

test('F-03 动态路径：带插值的生产 operation 必须显式登记并覆盖', () => {
  const { operations } = enumerateProductionOperations();
  const dynamic = [...operations.values()].filter((op) => op.dynamic).map((op) => `${op.method} ${op.path}`);
  // 今天（round3 删除 endpoints/notification-actions 之后）生产侧没有动态契约路径。
  // 一旦出现（例如 `/pc/endpoints/{deviceId}/...`），必须先补契约用例再更新这里，
  // 不允许“拼出来的路径绕过契约检查”。
  assert.deepEqual(
    dynamic,
    [],
    `发现未登记的动态契约路径，必须先补覆盖并在本检查里显式登记：\n  ${dynamic.join('\n  ')}`,
  );
});

test('F-03 排除项：本地非 PIM 端点的来源与输出都是真实的（M-5）', () => {
  const { excluded } = enumerateProductionOperations();

  assert.ok(excluded.length > 0, '排除清单为空：本地 /api/stats/ 调用没有被枚举出来，排除分支又成了死路径');
  for (const entry of excluded) {
    assert.ok(entry.site && entry.raw !== undefined && entry.why, `排除项缺少来源/原因：${JSON.stringify(entry)}`);
    assert.ok(
      EXCLUDED_LOCAL_PREFIXES.some((e) => e.prefix === entry.prefix),
      `出现了未登记的排除前缀：${entry.prefix} ← ${entry.site}`,
    );
  }

  // 逐条断言来源（不是“有排除这回事”而已）：KeyStats 本地统计服务的两个真实调用点。
  const sites = excluded.map((e) => `${e.prefix} ${e.site}`);
  assert.ok(
    sites.some((s) => s === '/api/stats/ src/client-windows/Pim.Client.Core/Services/KeyStatsLocalStatsClient.cs:38'),
    `没有枚举到 KeyStatsLocalStatsClient 的 /api/stats/ 调用；实际：${JSON.stringify(sites)}`,
  );
  assert.ok(
    sites.some((s) => s === '/api/stats/ src/client-windows/Pim.Client.Core/Services/KeyStatsCollectorService.cs:102'),
    `没有枚举到 KeyStatsCollectorService 的 /api/stats/ 调用；实际：${JSON.stringify(sites)}`,
  );

  // 输出真实：摘要里必须逐条包含分母、排除项与覆盖计数（round4 的脚本什么都不打印）。
  const summary = formatSummary();
  assert.match(summary, /\[F-03\] 生产 operation 分母：11 条/);
  assert.match(summary, /\[F-03\] 排除的本地非 PIM 端点：\d+ 条/);
  assert.match(summary, /\[F-03\]   排除 \/api\/stats\//);
  assert.match(summary, /\[F-03\] 编译中契约用例覆盖：11\/11/);
  console.log(summary);
});

test('F-03 覆盖：每个生产 operation 都有参与编译的契约用例（call 级判据）', () => {
  const { operations } = enumerateProductionOperations();
  const covered = enumerateContractCoverage();
  const missing = uncoveredOperations(operations.keys(), covered);

  const detail = missing.map((key) => {
    const op = operations.get(key);
    return `${key}  ← ${op.sites.join(', ')}`;
  });

  assert.deepEqual(
    missing,
    [],
    '以下生产 operation 没有任何参与编译的契约用例（没有「生产入口 → 捕获真实字节 → 对照 contract/openapi.json」的验证）：\n  '
      + detail.join('\n  '),
  );
});

test('F-03 覆盖检查不是空检查：合成缺口必须被识别、注释里的调用不算覆盖', () => {
  // ① 覆盖判定器本身的正/反例（用合成分母，不受真实 tree 状态影响）。
  const synthetic = ['post /api/v1/a', 'get /api/v1/b'];
  assert.deepEqual(uncoveredOperations(synthetic, new Set(synthetic)), []);
  assert.deepEqual(uncoveredOperations(synthetic, new Set(['post /api/v1/a'])), ['get /api/v1/b']);
  assert.equal(uncoveredOperations(synthetic, new Set()).length, synthetic.length);

  // ② 真实覆盖枚举器必须能认出已知被覆盖的 operation —— 包括**路径写在常量里**的
  //    keystats 两个端点（round5 的常量解引用），否则“全部覆盖”失败可能是枚举器坏了。
  const covered = enumerateContractCoverage();
  for (const known of [
    'post /api/v1/daemon/heartbeat',
    'post /api/v1/daemon/planned-offline',
    'post /api/v1/pc/tracker/upload',
    'post /api/v1/pc/browser-tt/upload',
    'post /api/v1/pc/keystats/upload',
    'post /api/v1/pc/keystats/samples',
    'post /api/v1/auth/login',
    'post /api/v1/auth/refresh',
    'post /api/v1/pc/tracker/health',
    'get /health',
    'get /api/client/shell/latest',
  ]) {
    assert.ok(covered.has(known), `覆盖枚举器没有认出已有契约用例的 operation：${known}`);
  }

  // ③ 反控制：把契约调用写在**注释**里、或只出现路径字面量，都不得算覆盖（M-3）。
  const syntheticCoverageRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'pim-f03-cov-'));
  try {
    makeSyntheticProject(syntheticCoverageRoot, {
      project: TEST_PROJECTS[0],
      files: {
        'CommentedOnly.cs': [
          'class T {',
          '  // ContractOperation.ForPath("/api/v1/daemon/heartbeat", "post");',
          '  // 路径字面量出现，但调用被注释掉了：/api/v1/daemon/heartbeat',
          '  void Real() { }',
          '}',
        ].join('\n'),
      },
    });
    const fromComment = enumerateContractCoverage(syntheticCoverageRoot);
    assert.equal(
      fromComment.size,
      0,
      `注释里的契约调用被当成了覆盖：${JSON.stringify([...fromComment])}`,
    );

    // ④ 同一个合成工程里，把调用改成真的（并解引用同文件常量）后必须被认出。
    makeSyntheticProject(syntheticCoverageRoot, {
      project: TEST_PROJECTS[0],
      files: {
        'CommentedOnly.cs': [
          'class T {',
          '  private const string P = "/api/v1/daemon/heartbeat";',
          '  void Real() {',
          '    ContractOperation.ForPath(P, "post");',
          '    ContractOperation.ForOperation(',
          '      "/health", "get");',
          '  }',
          '}',
        ].join('\n'),
      },
    });
    const fromCode = enumerateContractCoverage(syntheticCoverageRoot);
    assert.deepEqual(
      [...fromCode].sort(),
      ['get /health', 'post /api/v1/daemon/heartbeat'],
      `真实调用（含常量与跨行）没有被认出：${JSON.stringify([...fromCode])}`,
    );
  } finally {
    fs.rmSync(syntheticCoverageRoot, { recursive: true, force: true });
  }
});
