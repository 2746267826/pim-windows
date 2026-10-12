# contract/openapi.json

本文件是 **`pim-api` 仓契约的只读快照**，仅供本仓开发时对照，**不要手工编辑**。

- 来源：`2746267826/pim-api` 的 `contract/openapi.json`（该仓 CI 从 Test 实例导出并硬校验与代码同步）
- 快照取自：`pim-api@940fe42face873e48c7c36af030d8ca838791471`（`master` 解析于 2026-10-12，938271 字节）
- 文件 sha256：`4dd7eafb4938818e13766e7177e1d05f45a4090643f9fe6dcd8d108c4f82af53`
- 机器可读来源记录：[`provenance.json`](provenance.json) —— `repo` / `ref` / `resolvedRef`（不可变
  40 位 SHA）/ `resolvedRefSource` / `sourcePath` / `sourceUrl` / `bytes` / `sha256`，**无时间戳**，
  由刷新脚本写出；其中 `bytes` / `sha256` 是**本仓快照字节**的哈希。

## 为什么要放进本仓

本仓需要调用后端接口，但**不要凭猜测调用**。契约里有什么，才能调什么；契约里没有的，
就不要写进代码 —— 那是「需要新接口」的信号，按 `AGENTS.md` 的「接口契约与门禁」一节
把需求交给用户，由后端在 `pim-api` 实现并更新契约。

## 谁写、谁只读

- `scripts/ci/fetch-contract.sh` —— **refresh（会写仓）**：把上游 `ref` 解析成**不可变 40 位 SHA**，
  按该 SHA 取**原始字节**，校验 payload 形状，全部通过后才安装
  `contract/openapi.json` 与 `contract/provenance.json`（任一校验失败 → 两个产物一个字节都不写）。
- `scripts/ci/check-contract-freshness.sh` —— **只读**：把仓内快照与上游当前 ref 的字节**逐字节比较**，
  并校验 `provenance.json` 的形状及其与快照的一致性。**绝不写仓**；CI 调的就是它。

## 怎么刷新

后端契约更新后（用户告知，或你发现契约里缺了已有字段）：

```bash
scripts/ci/fetch-contract.sh
```

脚本会把最新契约拉下来覆盖快照，并写出 `contract/provenance.json`。**刷新后请一并提交这两个文件**，
并在 PR 里说明刷新原因。要换来源/ref 时有 env 可覆盖（默认即上游 `master`）：
`PIM_CONTRACT_REPO`、`PIM_CONTRACT_REF`（分支 / tag / 40 位 SHA）、`PIM_CONTRACT_RAW_BASE`、
`PIM_CONTRACT_REMOTE`、`PIM_CONTRACT_SOURCE_PATH`。

## 注意

- **快照落后会让本仓 CI 变红（预期行为）**：`contract-freshness` job 做上述逐字节比较，
  并已接进必过的 `CI Gate`。上游契约一前进而本仓没刷新 → `CI Gate` 红；
  跑一次 `scripts/ci/fetch-contract.sh` 并提交两个产物即恢复。
- 取不到权威源（网络不可达 / 404 / 空体 / 非合法 OpenAPI 3.x）**一律按失败处理**，
  不会跳过、不会当成「最新」。
- 上游出现**非契约提交**、契约字节没变 → 不红；`provenance.resolvedRef` **允许滞后**
  于上游 HEAD（它记录刷新当时的来源 SHA，不参与新鲜度判定）。
- 上游契约一变，三仓（web / android / windows）的 freshness job 会**同时变红**，
  需要人工逐个刷新并各开 PR —— 这是预期的人工确认点，不是 CI 故障。
- 手写模型对不上契约时，编译/构建不会报错，问题只会在运行时出现。改接口相关代码前，先对照本文件。

## 手写模型一致性报告

刷新快照时同步核对了本仓手写 DTO 与契约的静态差异，**只报告、不修产品**：

- [`model-consistency.md`](model-consistency.md) —— 人读报告（13 个真实 operation 判定、security、覆盖口径、未验证项）
- [`model-consistency.detail.json`](model-consistency.detail.json) —— 机读详表（每条 finding 带
  `contractField.{required,readOnly,writeOnly,nullable,default,enum,ref,type}` 与 `requiredSemantics`，
  另有 `operationAudit` / `security` / `coverage` / `excludedFindings`）

注意：`required` 不区分方向；契约 schema 里 `readOnly: true` 且 `required` 的属性按 OpenAPI 3.0
**只对响应生效**，客户端不发它可能是正确行为，不能据此判定客户端 bug。
