#!/usr/bin/env node
/**
 * REQ-COM-02 / REQ-COM-06 · 审计 **F-10** 的「孤儿用例」分母与覆盖检查
 * （round4 建立，round5 按独立 review I-1 与迁移结果重写）。
 *
 * 审计 F-10：`tests/ClientWindows` 里有 4 个含契约/行为断言的用例文件**完全不被编译、
 * 不被执行**。round4 独立 review（`reviews/windows-round4-deepseek.md` I-1）把分母按**用例级**
 * 纠正为 **16 = 可移植 10 / 后端依赖 6**（不是 8/8）：
 *
 *   | 原文件 | 用例 | 可移植 | 后端依赖 |
 *   |---|---|---|---|
 *   | `BusinessDayUtilsTests.cs`       | 4 | 3 | 1（`Pim.Module.PcTracker`） |
 *   | `DaemonHeartbeatReporterTests.cs`| 5 | 4 | 1（`Pim.Core.Operations`） |
 *   | `PlannedOfflineReporterTests.cs` | 2 | 1 | 1（`Pim.Core.Operations`） |
 *   | `Wo398SuspendGapTests.cs`        | 5 | 2（行 66/93 纯客户端） | 3（`Pim.Core.Invariants`） |
 *
 * round5 的处置（本文件机械检查其结果）：
 *   ① 可移植 10 条已迁入**参与编译**的用例文件，并以同名 `[Fact]` 方法**真实存在**
 *      （判据是「方法声明 + xunit 属性 + 文件在某个工程的 Compile 清单里」，
 *      不是字符串 needle —— needle 可以被注释满足）；
 *   ② 后端依赖 6 条在本仓**无法编译**，因此保持 `NOT-VERIFIED`：本文件要求它们的**归属与
 *      真实验证条件**逐条出现在 `BACKEND-OWNED-CASES.md`，并且**不得**被伪造成客户端用例；
 *   ③ 磁盘上每个 `tests/ClientWindows/*.cs` 都必须被某个测试工程编译（不允许再出现孤儿文件）。
 *
 * 判据边界（必须如实记录，不得升格）：①②③ 证明的是「用例被编译、被登记、有归属」，
 * **不**证明每个断言都真的执行过 —— 执行证据是 `dotnet test` 的真实输出（见 round5 日志）。
 *
 * 用法：`node --test scripts/ci/test-orphan-test-coverage.mjs`
 * （已由既有 `ci-scripts` job 的 `node --test scripts/ci/test-*.mjs` 收集，无需新 job。）
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const CASES_DIR = 'tests/ClientWindows';
const TEST_PROJECTS = [
  'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
  'tests/Pim.Client.App.Tests/Pim.Client.App.Tests.csproj',
  'src/client-shell-windows/Pim.Shell.Tests/Pim.Shell.Tests.csproj',
];

/** 审计 F-10 点名的 4 个孤儿文件（round5 已删除；保留清单用于核对分母没被改名/删除）。 */
export const AUDITED_ORPHAN_FILES = [
  'BusinessDayUtilsTests.cs',
  'DaemonHeartbeatReporterTests.cs',
  'PlannedOfflineReporterTests.cs',
  'Wo398SuspendGapTests.cs',
];

/** 可移植 10 条：必须在**某个 TestProject 里**以同名 `[Fact]` 方法存在。 */
export const MIGRATED_PORTABLE_CASES = [
  { method: 'GetBusinessDate_BeforeFourAmShanghai_BelongsToPreviousCalendarDay', file: 'BusinessDayUtilsClientTests.cs' },
  { method: 'GetBusinessDate_AtOrAfterFourAmShanghai_BelongsToCurrentCalendarDay', file: 'BusinessDayUtilsClientTests.cs' },
  { method: 'GetNextBusinessDayStart_ReturnsNextFourAmShanghaiInUtc', file: 'BusinessDayUtilsClientTests.cs' },
  { method: 'BuildHeartbeat_UsesIpv4LoopbackDefaultServerUrl', file: 'DaemonHeartbeatReporterClientTests.cs' },
  { method: 'BuildHeartbeat_UsesDefaultServerUrlWhenBlank', file: 'DaemonHeartbeatReporterClientTests.cs' },
  { method: 'BuildHeartbeat_StatusJsonIncludesMachineAndProcess', file: 'DaemonHeartbeatReporterClientTests.cs' },
  { method: 'BuildHeartbeat_UsesProvidedSourceStatesAndStatusDetails', file: 'DaemonHeartbeatReporterClientTests.cs' },
  { method: 'BuildRequest_FillsDeviceKindAndReason', file: 'PlannedOfflineReporterClientTests.cs' },
  { method: 'Ac4_4_AliveButQuietFor40Minutes_WithoutDeclaration_EmitsGapChunksForTheWholeInterval', file: 'SuspendGapClientTests.cs' },
  { method: 'Ac4_4_SuspendThenResume_CoversTheWholeSleepIntervalWithGapChunks', file: 'SuspendGapClientTests.cs' },
];

export const BACKEND_OWNED_DOC = `${CASES_DIR}/BACKEND-OWNED-CASES.md`;

/**
 * 后端依赖 6 条：本仓不可编译 → 必须在 runbook 里逐条登记归属 + 真实验证条件，
 * 且**不得**出现在客户端编译用例里（否则就是伪造成客户端验证）。
 */
export const BACKEND_OWNED_CASES = [
  { method: 'ClientHeartbeatJson_DeserializesIntoApiRequestContract', owner: 'Pim.Core.Operations.DaemonHeartbeatRequest' },
  { method: 'BuildRequest_RoundTripsToServerDto', owner: 'Pim.Core.Operations.PlannedOfflineRequest' },
  { method: 'PcTrackerService_GetBusinessDayForTimestamp_MatchesBusinessDayUtils', owner: 'Pim.Module.PcTracker.Services.PcTrackerService' },
  { method: 'Ac4_4_PointDeclaration_DoesNotMaskALaterRealGap', owner: 'Pim.Core.Invariants.DataReliabilityInvariants' },
  { method: 'Ac4_4_SevenSecondRecoveryThenAGap_IsCoveredByTheExistingBoundedGrace', owner: 'Pim.Core.Invariants.DataReliabilityInvariants' },
  { method: 'Ac4_4_DeclarationCoveringTheSleepInterval_MarksThatGapAsDeclared', owner: 'Pim.Core.Invariants.DataReliabilityInvariants' },
];

function readIfExists(rel) {
  const abs = path.join(ROOT, rel);
  return fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
}

export function caseFilesOnDisk() {
  const abs = path.join(ROOT, CASES_DIR);
  if (!fs.existsSync(abs)) return [];
  return fs.readdirSync(abs).filter((f) => f.endsWith('.cs')).sort();
}

/** csproj 里的 `Compile Include` → 文件名集合（分隔符按 / 归一，兼容 Windows 写法）。 */
export function compiledCaseFiles() {
  const compiled = new Set();
  for (const relProject of TEST_PROJECTS) {
    const text = readIfExists(relProject);
    if (text === null) continue;
    for (const match of text.matchAll(/Compile Include="([^"]+)"/g)) {
      const normalized = match[1].split('\\').join('/');
      compiled.add(path.basename(normalized));
    }
  }
  return compiled;
}

export function orphanCaseFiles() {
  const compiled = compiledCaseFiles();
  return caseFilesOnDisk().filter((f) => !compiled.has(f));
}

/** 参与编译的测试源文件全文（按文件名索引；同名文件取并集）。 */
function compiledSources() {
  const out = new Map();
  for (const relProject of TEST_PROJECTS) {
    const text = readIfExists(relProject);
    if (text === null) continue;
    for (const match of text.matchAll(/Compile Include="([^"]+)"/g)) {
      const normalized = match[1].split('\\').join('/');
      const abs = path.normalize(path.join(path.dirname(path.join(ROOT, relProject)), normalized));
      if (!fs.existsSync(abs)) continue;
      const name = path.basename(abs);
      const body = fs.readFileSync(abs, 'utf8');
      out.set(name, out.has(name) ? `${out.get(name)}\n${body}` : body);
    }
  }
  return out;
}

/**
 * 从源码里取出**声明成 xunit 用例**的方法名：`[Fact]`/`[Theory]`（含 `[Theory]` 后紧跟
 * `[InlineData]` 的情形）之后出现的 `public [async] Task|void Name(`。
 * 这是「真的有一条会跑的用例」的机械代理判据，不是字符串 needle。
 */
export function declaredTestMethods(text) {
  const names = new Set();
  for (const attribute of text.matchAll(/\[(?:Fact|Theory)[^\]]*\]/g)) {
    const window = text.slice(attribute.index, attribute.index + 800);
    const declaration = /\bpublic\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(/.exec(window);
    if (declaration) names.add(declaration[1]);
  }
  return names;
}

/** 迁移用例缺口：方法没有以 xunit 用例形式出现在**指定文件**里时逐条报出。 */
export function missingMigratedCases(cases = MIGRATED_PORTABLE_CASES, sources = compiledSources()) {
  const missing = [];
  for (const item of cases) {
    const text = sources.get(item.file);
    if (text === undefined) {
      missing.push(`${item.method}（${item.file} 不在任何测试工程的 Compile 清单里）`);
      continue;
    }
    if (!declaredTestMethods(text).has(item.method)) {
      missing.push(`${item.method}（${item.file} 里没有同名的 [Fact]/[Theory] 用例声明）`);
    }
  }
  return missing;
}

// ---------------------------------------------------------------------------
// 用例
// ---------------------------------------------------------------------------

test('F-10 孤儿分母：tests/ClientWindows 下每个 .cs 都被某个测试工程编译', () => {
  const orphans = orphanCaseFiles();
  const detail = orphans.map((f) => {
    const audited = AUDITED_ORPHAN_FILES.includes(f) ? '（审计 F-10 点名）' : '（新出现的孤儿）';
    return `${CASES_DIR}/${f} ${audited}`;
  });
  assert.deepEqual(
    orphans,
    [],
    '以下用例文件不在任何测试工程的 Compile 清单里，等于不参与编译与执行：\n  '
      + detail.join('\n  ')
      + '\n修法：可移植断言改写成只依赖本仓类型并加入 csproj；'
      + '后端依赖用例登记到 BACKEND-OWNED-CASES.md（本仓无法编译，不得伪造成客户端验证）。',
  );
});

test('F-10 可移植覆盖：10 条迁移用例必须以真实 xunit 用例存在于编译中的文件', () => {
  const missing = missingMigratedCases();
  assert.deepEqual(
    missing,
    [],
    '以下可移植用例没有真正迁进参与编译的测试文件（孤儿文件被删掉不等于覆盖达成）：\n  '
      + missing.join('\n  '),
  );

  // 分母核对：16 = 可移植 10 + 后端 6，且原 4 个孤儿文件已不在磁盘上（改名会在这里露出来）。
  assert.equal(MIGRATED_PORTABLE_CASES.length, 10, '可移植分母必须是 10 条');
  assert.equal(BACKEND_OWNED_CASES.length, 6, '后端归属分母必须是 6 条');
  for (const orphan of AUDITED_ORPHAN_FILES) {
    assert.ok(
      !caseFilesOnDisk().includes(orphan),
      `审计点名的孤儿文件 ${orphan} 仍在磁盘上：要么迁移并删除，要么说明原因，不允许留着当“不执行的用例”`,
    );
  }
});

test('F-10 后端 6 条：归属与真实验证条件必须逐条登记，且不得伪造成客户端用例', () => {
  const doc = readIfExists(BACKEND_OWNED_DOC);
  assert.ok(doc !== null, `缺少 ${BACKEND_OWNED_DOC}：后端依赖用例的归属与验证条件必须落盘`);

  const missing = [];
  for (const item of BACKEND_OWNED_CASES) {
    if (!doc.includes(item.method)) missing.push(`${item.method}（runbook 里没有登记）`);
    else if (!doc.includes(item.owner)) missing.push(`${item.method}（runbook 里没有写出归属类型 ${item.owner}）`);
  }
  assert.deepEqual(missing, [], `后端依赖用例的归属/验证条件登记不完整：\n  ${missing.join('\n  ')}`);

  // runbook 必须说明“真实验证条件”（后端宿主的落地方式），不能只写一句 NOT-VERIFIED 了事。
  assert.match(doc, /真实验证条件/, 'runbook 没有给出真实验证条件');
  assert.match(doc, /NuGet|pim-api/, 'runbook 没有写清后端宿主/NuGet 契约包的落地方式');

  // 反控制：这 6 条**不得**以客户端用例形式出现在编译中的文件里（那是伪造验证）。
  const sources = compiledSources();
  const faked = [];
  for (const item of BACKEND_OWNED_CASES) {
    for (const [name, text] of sources) {
      if (declaredTestMethods(text).has(item.method)) faked.push(`${item.method} ← ${name}`);
    }
  }
  assert.deepEqual(
    faked,
    [],
    '后端依赖用例被伪装成客户端用例（本仓没有这些后端程序集，跑不了）：\n  ' + faked.join('\n  '),
  );
});

test('F-10 后端 DTO 维度：编译套件必须用契约 schema 覆盖这两个上报体', () => {
  // 孤儿文件里的两条「客户端序列化 → 服务端 DTO 反序列化」不能编译；
  // 编译中的等价物是 ContractWireShapeTests 直接对照 contract/openapi.json。
  // 这里钉住这个替代关系存在，避免“孤儿没了、覆盖也没了”。
  const sources = compiledSources();
  for (const [symbol, opPath] of [
    ['DaemonHeartbeatReporter', '/api/v1/daemon/heartbeat'],
    ['PlannedOfflineReporter', '/api/v1/daemon/planned-offline'],
  ]) {
    const hit = [...sources.values()].some(
      (text) => text.includes(symbol) && text.includes(opPath) && text.includes('ContractOperation'),
    );
    assert.ok(
      hit,
      `${symbol} 的真实线上字节没有被任何编译中的契约用例覆盖（期望引用 ${opPath} 的 ContractOperation 断言）`,
    );
  }
  // 说明（不写成断言）：真实的 pim-api DTO 反序列化仍是 NOT-VERIFIED，
  // 需要 pim-api 提供契约包或在 pim-api 的 CI 里跑那 6 条后端依赖用例。
});

test('F-10 判定器不是空检查：合成清单能被识别、迁移用例缺失能被发现', () => {
  // 反例：把磁盘清单换成合成清单后，孤儿检出必须按预期工作。
  const syntheticDisk = ['A.cs', 'B.cs'];
  const syntheticCompiled = new Set(['A.cs']);
  assert.deepEqual(syntheticDisk.filter((f) => !syntheticCompiled.has(f)), ['B.cs']);

  // 正例：全部编译时没有孤儿。
  assert.deepEqual(syntheticDisk.filter((f) => !new Set(syntheticDisk).has(f)), []);

  // 真仓库的编译清单必须非空，否则上面的孤儿结论可能是“csproj 没解析到”造成的假红。
  assert.ok(compiledCaseFiles().size >= 30, `编译清单只解析到 ${compiledCaseFiles().size} 个文件，解析器有问题`);

  // 合成需求：故意要求一个不存在的方法时必须报缺口（判据不是恒绿）。
  assert.deepEqual(
    missingMigratedCases([{ method: '__NEVER_IN_REPO__', file: 'ContractWireShapeTests.cs' }]),
    ['__NEVER_IN_REPO__（ContractWireShapeTests.cs 里没有同名的 [Fact]/[Theory] 用例声明）'],
  );

  // 合成需求：方法名对但文件不对时也必须报缺口（不能拿“别处有同名方法”顶替）。
  assert.deepEqual(
    missingMigratedCases([{ method: 'BuildRequest_FillsDeviceKindAndReason', file: 'ContractWireShapeTests.cs' }]).length,
    1,
  );

  // 声明提取器必须真的认识带属性/跨行的用例声明，且不把普通方法当用例。
  const sample = [
    'public class T {',
    '  [Fact]',
    '  public async Task RealCase()',
    '  {',
    '    await Task.CompletedTask;',
    '  }',
    '  [Theory]',
    '  [InlineData(1)]',
    '  public void TheoryCase(int x) { }',
    '  public void PlainHelper() { }',
    '}',
  ].join('\n');
  assert.deepEqual([...declaredTestMethods(sample)].sort(), ['RealCase', 'TheoryCase']);
});
