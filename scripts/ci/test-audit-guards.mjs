#!/usr/bin/env node
/**
 * 审计整改 · check-audit-guards.mjs 的**故障对照**。
 *
 * 目的：证明每条检查不是“恒红/恒绿”的空检查 —— 对每条检查都在隔离副本里
 *   ① 注入一个针对该检查的合成故障 → 必须报出对应 finding；
 *   ② 在副本里做最小“修复” → 必须不再报出该 finding。
 *
 * 注意：副本只用于静态判定，不会被真实构建/执行（release-logic 那条的“修复”因此
 * 允许是文本级的改动，只要满足被检查的不变量）。真实执行类的证据在
 * resolve-version.sh / test-release-assets.sh / check-dependency-hygiene.sh 与
 * C# 行为测试里。
 *
 * 用法：node scripts/ci/test-audit-guards.mjs
 * 退出码：0 = 全部对照通过；1 = 存在对照失败。
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

import { CHECKS } from './check-audit-guards.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');

const SKIP_DIRS = new Set(['.git', 'bin', 'obj', 'node_modules', 'dist', 'publish']);

function copyRepo(dest) {
  fs.cpSync(REPO_ROOT, dest, {
    recursive: true,
    filter: (src) => !SKIP_DIRS.has(path.basename(src)),
  });
}

let tempRoot = null;
function fixture() {
  if (tempRoot === null) {
    tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'pim-guard-fixture-'));
  }
  const dir = fs.mkdtempSync(path.join(tempRoot, 'case-'));
  copyRepo(dir);
  return dir;
}

function cleanup() {
  if (tempRoot !== null) {
    fs.rmSync(tempRoot, { recursive: true, force: true });
    tempRoot = null;
  }
}

const read = (root, rel) => fs.readFileSync(path.join(root, rel), 'utf8');
const write = (root, rel, text) => {
  fs.mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  fs.writeFileSync(path.join(root, rel), text);
};
const edit = (root, rel, from, to) => {
  const text = read(root, rel);
  if (!text.includes(from)) {
    throw new Error(`fixture 注入失败：${rel} 里找不到锚点 ${JSON.stringify(from.slice(0, 60))}`);
  }
  write(root, rel, text.replace(from, to));
};
const rm = (root, rel) => fs.rmSync(path.join(root, rel), { recursive: true, force: true });

/** 在副本里中和“故意保留”的真实 F-11 违反，便于对照只针对合成违反判定。 */
function neutralizeRealTokenInjection(root) {
  const rel = 'src/client-windows/Pim.Client.Core/Security/EmbeddedWebViewScripts.cs';
  const text = read(root, rel);
  const lines = text.split('\n').map((line) => (
    /__pimToken|sessionStorage\.setItem/.test(line) ? `// neutralized: ${line.trim()}` : line
  ));
  write(root, rel, lines.join('\n'));
}

function runCheck(id, root) {
  const check = CHECKS.find((c) => c.id === id);
  if (!check) throw new Error(`未注册的检查 ${id}`);
  try {
    return check.run(root) ?? [];
  } catch (error) {
    // 检查自身抛错也必须算“报出了违反项”，而不是让整个对照崩掉。
    return [{ finding: check.findings[0], message: `检查执行失败: ${error.message}` }];
  }
}

const violationsFor = (id, root, finding) =>
  runCheck(id, root).filter((v) => v.finding === finding);

/**
 * 每个用例：inject = 合成故障；fix = 最小修复（去掉故障并满足被检查的不变量）。
 * expectFinding = 该检查声明的 finding ID。
 */
const CASES = [
  {
    // F-03：真仓库契约用例已经完整，所以这里注入的是“把全量套件换成 --filter”这种
    // 会让门禁失去覆盖的修法 —— 检查必须报出来。
    id: 'contract-enforcement',
    only: 'filter-narrows-suite',
    expectFinding: 'F-03',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      '        run: dotnet test tests/Pim.Client.Tests/Pim.Client.Tests.csproj --logger "console;verbosity=minimal"',
      '        run: dotnet test tests/Pim.Client.Tests/Pim.Client.Tests.csproj --filter FullyQualifiedName~ContractWireShapeTests --logger "console;verbosity=minimal"',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      '        run: dotnet test tests/Pim.Client.Tests/Pim.Client.Tests.csproj --filter FullyQualifiedName~ContractWireShapeTests --logger "console;verbosity=minimal"',
      '        run: dotnet test tests/Pim.Client.Tests/Pim.Client.Tests.csproj --logger "console;verbosity=minimal"',
    ),
  },
  {
    // F-10：磁盘上真实存在的契约用例被移出 Compile 清单 → 必须报。
    id: 'contract-roundtrip-compiled',
    only: 'contract-case-removed-from-compile',
    expectFinding: 'F-10',
    inject: (root) => edit(
      root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
      '    <Compile Include="../ClientWindows/ContractWireShapeTests.cs" />\n',
      '',
    ),
    fix: (root) => edit(
      root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
      '    <Compile Include="../ClientWindows/HeartbeatQueueDepthTests.cs" />',
      '    <Compile Include="../ClientWindows/ContractWireShapeTests.cs" />\n    <Compile Include="../ClientWindows/HeartbeatQueueDepthTests.cs" />',
    ),
  },
  {
    // F-10：csproj 写了文件名、磁盘上没有该文件（“写文件名≠编译”）。
    id: 'contract-roundtrip-compiled',
    only: 'csproj-names-a-missing-file',
    expectFinding: 'F-10',
    inject: (root) => edit(
      root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
      '    <Compile Include="../ClientWindows/ContractWireShapeTests.cs" />\n',
      '    <Compile Include="../ClientWindows/ContractWireShapeTests.cs" />\n    <Compile Include="../ClientWindows/GhostContractTests.cs" />\n',
    ),
    fix: (root) => edit(
      root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj',
      '    <Compile Include="../ClientWindows/GhostContractTests.cs" />\n',
      '',
    ),
  },
  {
    // F-05：把 release.needs 退回只含 3 个 job 的形态。
    id: 'release-gate',
    only: 'needs-missing-required-jobs',
    expectFinding: 'F-05',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      'needs: [resolve-version, build-windows, build-shell-windows, build-browser-extension, build-browser-extension-firefox, test-client-core, test-client-app, release-assets, browser-extension-lint, contract-freshness, ci-scripts, codeql, pr-metadata, gate]',
      'needs: [resolve-version, build-windows, build-shell-windows]',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      'needs: [resolve-version, build-windows, build-shell-windows]',
      'needs: [resolve-version, build-windows, build-shell-windows, build-browser-extension, build-browser-extension-firefox, test-client-core, test-client-app, release-assets, browser-extension-lint, contract-freshness, ci-scripts, codeql, pr-metadata, gate]',
    ),
  },
  {
    // F-05（审计验收漏洞本身）：job 已列进 needs，但 if 里丢掉 failure 子句 ——
    // 测试失败仍会发版，检查必须报出来。
    id: 'release-gate',
    only: 'failure-clause-removed',
    expectFinding: 'F-05',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      "      !contains(needs.*.result, 'failure') &&\n",
      '',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      "      needs.resolve-version.outputs.is_release == 'true' &&\n",
      "      needs.resolve-version.outputs.is_release == 'true' &&\n      !contains(needs.*.result, 'failure') &&\n",
    ),
  },
  {
    // F-05（审计验收漏洞本身）：skipped 不阻止发布。
    id: 'release-gate',
    only: 'skipped-clause-removed',
    expectFinding: 'F-05',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      "      !contains(needs.*.result, 'skipped') &&\n",
      '',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      "      !contains(needs.*.result, 'cancelled')\n",
      "      !contains(needs.*.result, 'skipped') &&\n      !contains(needs.*.result, 'cancelled')\n",
    ),
  },
  {
    // F-06：把真实的行为测试从 PR job 里摘掉，只留一个引用脚本名的空壳。
    id: 'release-logic-runs-on-pr',
    only: 'behavior-test-not-run-on-pr',
    expectFinding: 'F-06',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      '        run: bash scripts/ci/test-release-assets.sh',
      '        run: echo "scripts/ci/test-release-assets.sh"',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      '        run: echo "scripts/ci/test-release-assets.sh"',
      '        run: bash scripts/ci/test-release-assets.sh',
    ),
  },
  {
    // F-06：行为测试脚本本身被删掉（只剩产品脚本 + 一个 echo）。
    id: 'release-logic-runs-on-pr',
    only: 'behavior-test-script-missing',
    expectFinding: 'F-06',
    inject: (root) => {
      rm(root, 'scripts/ci/test-release-assets.sh');
      edit(
        root, '.github/workflows/ci.yml',
        '        run: bash scripts/ci/test-release-assets.sh',
        '        run: echo "no behavior test"',
      );
    },
    fix: (root) => {
      // 修复必须是**真的**行为测试：空壳脚本（echo ok / exit 0）不再被接受
      // （check-release-logic-tested.sh 会用生产故障对照证明测试有效）。
      write(
        root,
        'scripts/ci/test-release-assets.sh',
        read(REPO_ROOT, 'scripts/ci/test-release-assets.sh'),
      );
      edit(
        root, '.github/workflows/ci.yml',
        '        run: echo "no behavior test"',
        '        run: bash scripts/ci/test-release-assets.sh',
      );
    },
  },
  {
    // F-22：is_release 只看 REF（手动触发也会发版）。
    id: 'release-trigger',
    only: 'dispatch-on-master-releases',
    expectFinding: 'F-22',
    inject: (root) => edit(
      root, 'scripts/ci/resolve-version.sh',
      'if [[ "$REF" == "refs/heads/master" && "$EVENT" == "push" ]]; then',
      'if [[ "$REF" == "refs/heads/master" ]]; then',
    ),
    fix: (root) => edit(
      root, 'scripts/ci/resolve-version.sh',
      'if [[ "$REF" == "refs/heads/master" ]]; then',
      'if [[ "$REF" == "refs/heads/master" && "$EVENT" == "push" ]]; then',
    ),
  },
  {
    // F-06（windows-red-grok I-3 的假绿）：把行为测试换成空壳 `exit 0` ——
    // 只做“job 里出现了脚本名”的字符串检查会放行，实际执行故障对照才会红。
    id: 'release-logic-runs-on-pr',
    only: 'empty-behavior-test-self-certifies',
    expectFinding: 'F-06',
    inject: (root) => write(root, 'scripts/ci/test-release-assets.sh', '#!/usr/bin/env bash\nexit 0\n'),
    fix: (root) => write(
      root,
      'scripts/ci/test-release-assets.sh',
      read(REPO_ROOT, 'scripts/ci/test-release-assets.sh'),
    ),
  },
  {
    // F-22：force_all 死参数回流。
    id: 'release-trigger',
    only: 'force-all-dead-input',
    expectFinding: 'F-22',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      '  workflow_dispatch:\n',
      '  workflow_dispatch:\n    inputs:\n      force_all:\n        description: Force all builds\n        required: false\n        default: true\n        type: boolean\n',
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      '    inputs:\n      force_all:\n        description: Force all builds\n        required: false\n        default: true\n        type: boolean\n',
      '',
    ),
  },
  {
    // F-08：KeyStats clone 未锁 SHA + 源码缺失 fail-open。
    id: 'supply-chain-pins',
    only: 'keystats-unpinned-and-fail-open',
    expectFinding: 'F-08',
    inject: (root) => {
      // 退回到审计时的形态：clone 默认分支、源码缺失只 Warning 后继续。
      edit(
        root, '.github/workflows/build-windows.yml',
        '      - name: Fetch pinned KeyStats source\n        shell: pwsh\n        env:\n          KEYSTATS_REPO: https://github.com/2746267826/keyStats.git\n          KEYSTATS_COMMIT: 5a2f5242170904a638f95ea9860b6231ac911b4d\n        run: |\n          if (Test-Path .keyStats-tmp) { Remove-Item -Recurse -Force .keyStats-tmp }\n          git clone --no-checkout $env:KEYSTATS_REPO .keyStats-tmp\n          if ($LASTEXITCODE -ne 0) { Write-Error "KeyStats clone failed"; exit 1 }\n          # 只接受 40 位十六进制提交号；分支/标签等可变 ref 一律拒绝。\n          if ($env:KEYSTATS_COMMIT -notmatch \'^[0-9a-f]{40}$\') {\n            Write-Error "KEYSTATS_COMMIT must be a full 40-hex commit SHA, got \'$env:KEYSTATS_COMMIT\'"\n            exit 1\n          }\n          git -C .keyStats-tmp checkout --detach $env:KEYSTATS_COMMIT\n          if ($LASTEXITCODE -ne 0) {\n            Write-Error "KeyStats commit $env:KEYSTATS_COMMIT not found; refusing to build an unpinned dependency"\n            exit 1\n          }\n          $actual = (git -C .keyStats-tmp rev-parse HEAD).Trim()\n          if ($actual -ne $env:KEYSTATS_COMMIT) {\n            Write-Error "KeyStats HEAD is $actual, expected $env:KEYSTATS_COMMIT"\n            exit 1\n          }\n',
        '      - name: Fetch KeyStats source\n        shell: pwsh\n        run: |\n          git clone --depth 1 https://github.com/2746267826/keyStats.git .keyStats-tmp\n          if (-not (Test-Path ".keyStats-tmp/KeyStats.Windows/KeyStats/KeyStats.csproj")) {\n            Write-Warning "KeyStats source not found; skipping build"\n            return\n          }\n',
      );
    },
    fix: (root) => {
      write(root, '.github/workflows/build-windows.yml', read(root, '.github/workflows/build-windows.yml')
        .replace(
          '      - name: Fetch KeyStats source\n        shell: pwsh\n        run: |\n          git clone --depth 1 https://github.com/2746267826/keyStats.git .keyStats-tmp\n          if (-not (Test-Path ".keyStats-tmp/KeyStats.Windows/KeyStats/KeyStats.csproj")) {\n            Write-Warning "KeyStats source not found; skipping build"\n            return\n          }\n',
          '      - name: Fetch KeyStats source\n        shell: pwsh\n        env:\n          KEYSTATS_COMMIT: 5a2f5242170904a638f95ea9860b6231ac911b4d\n        run: |\n          git clone https://github.com/2746267826/keyStats.git .keyStats-tmp\n          git -C .keyStats-tmp checkout --detach $env:KEYSTATS_COMMIT\n          $actual = (git -C .keyStats-tmp rev-parse HEAD).Trim()\n          if ($actual -ne $env:KEYSTATS_COMMIT) { exit 1 }\n          if (-not (Test-Path ".keyStats-tmp/KeyStats.Windows/KeyStats/KeyStats.csproj")) {\n            Write-Error "KeyStats source not found"\n            exit 1\n          }\n',
        ));
    },
  },
  {
    // F-09：外部 checkout 的 ref 回退到可变分支，且没有 fail-closed 验证。
    id: 'supply-chain-pins',
    only: 'external-ref-falls-back-to-branch',
    expectFinding: 'F-09',
    inject: (root) => {
      edit(
        root, '.github/workflows/build-windows.yml',
        '          ref: ${{ inputs.site_extension_ref }}\n',
        "          ref: ${{ inputs.site_extension_ref || 'main' }}\n",
      );
    },
    fix: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      "          ref: ${{ inputs.site_extension_ref || 'main' }}\n",
      '          ref: ${{ inputs.site_extension_ref }}\n',
    ),
  },
  {
    // F-08（windows-red-grok I-3 的假绿）：SHA 仍留在环境变量里，但 checkout 换成可变分支。
    // 旧判据只要求“窗口里同时出现过 40 位 hex 和 git checkout”，这种形态会漏过。
    id: 'supply-chain-pins',
    only: 'keystats-checkout-not-bound-to-sha',
    expectFinding: 'F-08',
    inject: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      '          git -C .keyStats-tmp checkout --detach $env:KEYSTATS_COMMIT\n',
      '          git -C .keyStats-tmp checkout main\n',
    ),
    fix: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      '          git -C .keyStats-tmp checkout main\n',
      '          git -C .keyStats-tmp checkout --detach $env:KEYSTATS_COMMIT\n',
    ),
  },
  {
    // F-09（Grok C-1）：workflow_dispatch 的站点 ref 默认值仍是可变分支 main ——
    // “只查文件里第一处 site_extension_ref 默认值”会漏掉这个入口。
    id: 'supply-chain-pins',
    only: 'site-ref-dispatch-default-branch',
    expectFinding: 'F-09',
    inject: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      "        description: 'Site-level extension ref（只接受 40 位提交号，非 40 位一律失败）'\n        required: false\n",
      "        description: 'Site-level extension ref（只接受 40 位提交号，非 40 位一律失败）'\n        required: false\n        default: 'main'\n",
    ),
    fix: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      "        default: 'main'\n",
      '',
    ),
  },
  {
    // F-09：检出后没有复核 HEAD —— “有验证 step”但验的不是检出的东西。
    id: 'supply-chain-pins',
    only: 'site-ref-not-rebound-after-checkout',
    expectFinding: 'F-09',
    inject: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      '          actual="$(git -C "$GITHUB_WORKSPACE/.extension-site" rev-parse HEAD)"\n          if [ "$actual" != "${{ inputs.site_extension_ref }}" ]; then\n            echo "::error::site-level extension HEAD is $actual, expected ${{ inputs.site_extension_ref }}"\n            exit 1\n          fi\n',
      '',
    ),
    fix: (root) => edit(
      root, '.github/workflows/build-windows.yml',
      '      - name: Setup Node for browser extensions',
      '          actual="$(git -C "$GITHUB_WORKSPACE/.extension-site" rev-parse HEAD)"\n          if [ "$actual" != "${{ inputs.site_extension_ref }}" ]; then\n            echo "::error::site-level extension HEAD is $actual, expected ${{ inputs.site_extension_ref }}"\n            exit 1\n          fi\n\n      - name: Setup Node for browser extensions',
    ),
  },
  {
    // F-09（Grok C-1 的原始形态）：checkout 在 $GITHUB_WORKSPACE 下，
    // 但 git -C 用相对路径、step 的 cwd 又是 src/client-windows —— 必然 exit 128。
    id: 'supply-chain-pins',
    only: 'relative-git-c-against-workspace-checkout',
    expectFinding: 'F-09',
    inject: (root) => {
      edit(
        root, '.github/workflows/build-windows.yml',
        '        working-directory: ${{ github.workspace }}\n        run: |\n          case "${{ inputs.site_extension_ref }}" in',
        '        run: |\n          case "${{ inputs.site_extension_ref }}" in',
      );
      edit(
        root, '.github/workflows/build-windows.yml',
        '          actual="$(git -C "$GITHUB_WORKSPACE/.extension-site" rev-parse HEAD)"',
        '          actual="$(git -C .extension-site rev-parse HEAD)"',
      );
    },
    fix: (root) => {
      edit(
        root, '.github/workflows/build-windows.yml',
        '          actual="$(git -C .extension-site rev-parse HEAD)"',
        '          actual="$(git -C "$GITHUB_WORKSPACE/.extension-site" rev-parse HEAD)"',
      );
      edit(
        root, '.github/workflows/build-windows.yml',
        '        run: |\n          case "${{ inputs.site_extension_ref }}" in',
        '        working-directory: ${{ github.workspace }}\n        run: |\n          case "${{ inputs.site_extension_ref }}" in',
      );
    },
  },
  {
    // F-16：README 声明的用例文件数与 csproj 实际清单不一致。
    id: 'test-inventory',
    only: 'readme-count-drifts',
    expectFinding: 'F-16',
    // 刻意不写死“正确数字”：从 csproj 清单推出真实数量，再注入一个错的。
    inject: (root) => {
      const csproj = read(root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj');
      const actual = (csproj.match(/<Compile Include=/g) ?? []).length;
      const wrong = actual + 7;
      edit(
        root, 'tests/ClientWindows/README.md',
        `| \`../Pim.Client.Tests\` | \`net8.0\` | ${actual} 个文件`,
        `| \`../Pim.Client.Tests\` | \`net8.0\` | ${wrong} 个文件`,
      );
    },
    fix: (root) => {
      const csproj = read(root, 'tests/Pim.Client.Tests/Pim.Client.Tests.csproj');
      const actual = (csproj.match(/<Compile Include=/g) ?? []).length;
      edit(
        root, 'tests/ClientWindows/README.md',
        `| \`../Pim.Client.Tests\` | \`net8.0\` | ${actual + 7} 个文件`,
        `| \`../Pim.Client.Tests\` | \`net8.0\` | ${actual} 个文件`,
      );
    },
  },
  {
    // F-12：prettier 存在于根 workflow 文本里，但 job 级条件把 PR 排除在外
    // （等价于“写在永不执行的 job 里”）。
    id: 'root-static-analysis',
    only: 'prettier-job-not-on-pull-request',
    expectFinding: 'F-12',
    inject: (root) => edit(
      root, '.github/workflows/ci.yml',
      '  browser-extension-lint:\n    needs: [resolve-version]\n',
      "  browser-extension-lint:\n    if: github.event_name == 'push'\n    needs: [resolve-version]\n",
    ),
    fix: (root) => edit(
      root, '.github/workflows/ci.yml',
      "  browser-extension-lint:\n    if: github.event_name == 'push'\n    needs: [resolve-version]\n",
      '  browser-extension-lint:\n    needs: [resolve-version]\n',
    ),
  },
  {
    // F-12：CodeQL 装配在根 workflow 里，但没有参与任何门禁（没人消费）。
    // codeql 现在同时出现在 gate.needs 与 release.needs，所以“没人消费”的合成故障
    // 必须把两处都摘掉，否则 gate 侧仍在消费、检查不该报。
    id: 'root-static-analysis',
    only: 'codeql-not-consumed-by-a-gate',
    expectFinding: 'F-12',
    inject: (root) => {
      edit(
        root, '.github/workflows/ci.yml',
        'ci-scripts, pr-metadata, codeql]',
        'ci-scripts, pr-metadata]',
      );
      edit(
        root, '.github/workflows/ci.yml',
        'ci-scripts, codeql, pr-metadata, gate]',
        'ci-scripts, pr-metadata, gate]',
      );
    },
    fix: (root) => {
      edit(
        root, '.github/workflows/ci.yml',
        'ci-scripts, pr-metadata]',
        'ci-scripts, pr-metadata, codeql]',
      );
      edit(
        root, '.github/workflows/ci.yml',
        'ci-scripts, pr-metadata, gate]',
        'ci-scripts, codeql, pr-metadata, gate]',
      );
    },
  },
  {
    // F-12：嵌套目录里的 workflow 回流（GitHub 永不执行，却看起来像门禁）。
    id: 'root-static-analysis',
    only: 'nested-workflows-return',
    expectFinding: 'F-12',
    inject: (root) => write(
      root,
      'browser-extension/.github/workflows/lint.yml',
      ['name: Lint', 'on:', '  pull_request:', 'jobs:', '  lint:', '    runs-on: ubuntu-latest', '    steps:', '      - run: npx prettier --check .', ''].join('\n'),
    ),
    fix: (root) => rm(root, 'browser-extension/.github'),
  },
  {
    // F-14：PR 模板引用了不存在的文档。
    id: 'docs-consistency',
    only: 'pr-template-broken-doc-link',
    expectFinding: 'F-14',
    inject: (root) => edit(
      root, '.github/pull_request_template.md',
      '见 docs/ai-dev-workflow.md',
      '见 docs/ai-dev-workflow.md 与 docs/does-not-exist.md',
    ),
    fix: (root) => edit(
      root, '.github/pull_request_template.md',
      '见 docs/ai-dev-workflow.md 与 docs/does-not-exist.md',
      '见 docs/ai-dev-workflow.md',
    ),
  },
  {
    // F-15：文档声称 docs-only 会跳过构建，但 ci.yml 没有 paths 过滤。
    id: 'docs-consistency',
    only: 'docs-only-claim-vs-no-paths-filter',
    expectFinding: 'F-15',
    inject: (root) => edit(
      root, 'AGENTS.md',
      '- Docs-only merges are **not** special-cased:',
      '- Docs-only merges skip all platform builds and do not produce a GitHub Release (path-filtered); they are special-cased:',
    ),
    fix: (root) => edit(
      root, 'AGENTS.md',
      '- Docs-only merges skip all platform builds and do not produce a GitHub Release (path-filtered); they are special-cased:',
      '- Docs-only merges are **not** special-cased:',
    ),
  },
  {
    // F-17：AGENTS.md 又把 .slnx 写成要执行的命令。
    id: 'docs-consistency',
    only: 'slnx-as-gate-command',
    expectFinding: 'F-17',
    inject: (root) => edit(
      root, 'AGENTS.md',
      'Run `dotnet build tests/Pim.Client.Tests/Pim.Client.Tests.csproj` for daemon changes',
      'Run `dotnet build src/client-windows/Pim.Client.Windows.slnx` and `dotnet build tests/Pim.Client.Tests/Pim.Client.Tests.csproj` for daemon changes',
    ),
    fix: (root) => edit(
      root, 'AGENTS.md',
      'Run `dotnet build src/client-windows/Pim.Client.Windows.slnx` and `dotnet build tests/Pim.Client.Tests/Pim.Client.Tests.csproj` for daemon changes',
      'Run `dotnet build tests/Pim.Client.Tests/Pim.Client.Tests.csproj` for daemon changes',
    ),
  },
  {
    // F-19：README 漏列一个工作流。
    id: 'docs-consistency',
    only: 'readme-missing-workflow',
    expectFinding: 'F-19',
    // README 里 ci.yml 可能出现多次（目录表 + 门禁章节），所以整体改名而不是替换一处。
    inject: (root) => write(root, 'README.md', read(root, 'README.md').split('ci.yml').join('renamed.yml')),
    fix: (root) => write(root, 'README.md', read(REPO_ROOT, 'README.md')),
  },
  {
    // F-11：生产源码把令牌交给 WebView 的脚本执行环境。
    //
    // 注意：真实仓库里这条违反是**故意保留**的（F-11 产品认证实现尚未获批，
    // 不允许靠“删掉注入”让守卫变绿）。因此本对照先在副本里把真实违反中和掉，
    // 再注入一条**合成**违反，最后只移除合成的那一条 —— 这样“特异性”仍然成立。
    id: 'webview-token-policy',
    expectFinding: 'F-11',
    inject: (root) => {
      neutralizeRealTokenInjection(root);
      write(
        root,
        'src/client-windows/Pim.Client.App/AuditGuardFixtureHost.cs',
        'namespace Pim.Client.App;\ninternal static class AuditGuardFixtureHost\n{\n    public static void Go(string tok) => System.Console.WriteLine($"window.__pimToken={tok}");\n}\n',
      );
    },
    fix: (root) => rm(root, 'src/client-windows/Pim.Client.App/AuditGuardFixtureHost.cs'),
  },
  {
    // F-21：新增一个没有任何调用者的公开成员（死代码），检查必须报出来。
    // round3 删除了旧豁免清单（“预留/将来”式理由不算用户豁免），
    // 所以最小修复是**删除**，而不是再登记一条豁免。
    id: 'dead-code',
    only: 'public-member-without-caller',
    expectFinding: 'F-21',
    inject: (root) => edit(
      root, 'src/client-windows/Pim.Client.Core/Services/ApiClient.cs',
      '    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)',
      '    public Task<ApiResponse<string>?> AuditGuardFixtureAsync(CancellationToken ct = default)\n        => GetAsync<ApiResponse<string>>("/audit-guard-fixture", ct);\n\n    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)',
    ),
    fix: (root) => edit(
      root, 'src/client-windows/Pim.Client.Core/Services/ApiClient.cs',
      '    public Task<ApiResponse<string>?> AuditGuardFixtureAsync(CancellationToken ct = default)\n        => GetAsync<ApiResponse<string>>("/audit-guard-fixture", ct);\n\n',
      '',
    ),
  },
  {
    // F-21：豁免记录必须逐条写明理由，不能变成无理由的静默忽略。
    id: 'dead-code',
    only: 'exemption-without-reason',
    expectFinding: 'F-21',
    inject: (root) => {
      edit(
        root, 'src/client-windows/Pim.Client.Core/Services/ApiClient.cs',
        '    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)',
        '    public Task<ApiResponse<string>?> AuditGuardFixtureAsync(CancellationToken ct = default)\n        => GetAsync<ApiResponse<string>>("/audit-guard-fixture", ct);\n\n    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)',
      );
      write(root, 'scripts/ci/dead-code-allowlist.json', JSON.stringify({
        exempt: [{ member: 'ApiClient.AuditGuardFixtureAsync' }],
      }, null, 2));
    },
    fix: (root) => edit(
      root, 'src/client-windows/Pim.Client.Core/Services/ApiClient.cs',
      '    public Task<ApiResponse<string>?> AuditGuardFixtureAsync(CancellationToken ct = default)\n        => GetAsync<ApiResponse<string>>("/audit-guard-fixture", ct);\n\n',
      '',
    ),
  },

];

function main() {
  const failures = [];

  for (const testCase of CASES) {
    const label = testCase.only ? `${testCase.id} [${testCase.only}]` : testCase.id;
    const check = CHECKS.find((c) => c.id === testCase.id);
    if (!check) {
      failures.push(`${label}: 检查未注册`);
      continue;
    }

    // ① 灵敏度：注入合成故障后，必须报出该 finding。
    const injected = fixture();
    testCase.inject(injected);
    const afterInject = violationsFor(testCase.id, injected, testCase.expectFinding);
    if (afterInject.length === 0) {
      failures.push(`${label}: 注入合成故障后没有报出 ${testCase.expectFinding}（检查对目标缺陷不敏感）`);
    } else {
      console.log(`OK   ${label}: 注入故障后报出 ${afterInject.length} 项 ${testCase.expectFinding}`);
    }

    // ② 特异性：做最小修复后，该 finding 必须消失。
    const fixed = fixture();
    testCase.inject(fixed);
    testCase.fix(fixed);
    const afterFix = violationsFor(testCase.id, fixed, testCase.expectFinding);
    if (afterFix.length > 0) {
      failures.push(`${label}: 最小修复后仍报出 ${testCase.expectFinding}（检查认不出修复）：${afterFix[0].message}`);
    } else {
      console.log(`OK   ${label}: 最小修复后不再报出 ${testCase.expectFinding}`);
    }
  }

  cleanup();

  if (failures.length > 0) {
    console.error('');
    for (const failure of failures) console.error(`FAIL ${failure}`);
    console.error('');
    console.error(`${failures.length} 项故障对照失败`);
    return 1;
  }

  console.log('');
  console.log(`${CASES.length} 项故障对照全部通过`);
  return 0;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  process.exit(main());
}
