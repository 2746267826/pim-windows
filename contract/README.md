# contract/openapi.json

本文件是 **`pim-api` 仓契约的只读快照**，仅供本仓开发时对照，**不要手工编辑**。

- 来源：`2746267826/pim-api` 的 `contract/openapi.json`（该仓 CI 从 Test 实例导出并硬校验与代码同步）
- 当前快照：`pim-api@4088bc446d4d98bbe20bee8b9241be2dfeabe78f`（`master`），941361 字节，
  sha256 `88a9d30066860765d23f9f5d10cd7f829e2cf3c6a957d173b02620b812da993b`
- 机器可读来源记录：[`provenance.json`](provenance.json) —— `repo` / `ref` / `resolvedRef`（不可变
  40 位 SHA）/ `resolvedRefSource` / `sourcePath` / `sourceUrl` / `bytes` / `sha256`，**无时间戳**，
  由刷新脚本写出；其中 `bytes` / `sha256` 是**本仓快照字节**的哈希。**以它为唯一权威**（上面的数字
  只是快照当时的记录，``check-contract-freshness.sh`` 校验的是 provenance 与快照字节，不是这段文字）。

## 为什么要放进本仓

本仓需要调用后端接口，但**不要凭猜测调用**。契约里有什么，才能调什么；契约里没有的，
就不要写进代码 —— 那是「需要新接口」的信号，按 `AGENTS.md` 的「接口契约与门禁」一节
把需求交给用户，由后端在 `pim-api` 实现并更新契约。

## 谁写、谁只读

契约侧（快照）：

- `scripts/ci/fetch-contract.sh` —— **refresh（会写仓）**：把上游 `ref` 解析成**不可变 40 位 SHA**，
  按该 SHA 取**原始字节**，校验 payload 形状，全部通过后才安装
  `contract/openapi.json` 与 `contract/provenance.json`（任一校验失败 → 两个产物一个字节都不写）。
- `scripts/ci/check-contract-freshness.sh` —— **只读**：把仓内快照与上游当前 ref 的字节**逐字节比较**，
  并校验 `provenance.json` 的形状及其与快照的一致性。**绝不写仓**；CI 调的就是它。

生成物侧（C# 接口边界）：

- `scripts/contract/generate-csharp.py` —— 生成器（只用 python3 标准库，离线、结果确定）。
- `scripts/ci/generate-contract-csharp.sh` —— **writer（会写仓）**：从快照 + operation 清单重新生成
  `src/client-windows/Pim.Client.Core/Contract/ContractModels.cs`（契约 DTO）与
  `ContractOperations.cs`（operation 与路径）。**生成物不可手工修改。**
- `scripts/ci/check-contract-csharp.sh` —— **只读**：用仓内快照重新生成一次，与仓内产物**逐字节比对**。
  **绝不写仓**；CI 的 `contract-generated` job 调的就是它。
- `scripts/ci/test-contract-csharp.sh` —— 该门禁的行为测试（离线 fixtures + 负对照），证明它不是「永远
  PASS 的摆设」：手工改生成物 / 生成物缺失 / 契约变了但产物没重生成 → 一律红。

## 怎么刷新

**正常情况下不需要人工执行**：`pim-api` 的 CI 在 `contract/openapi.json` 变化（且通过了它的
契约硬校验）后会向本仓发 `repository_dispatch`，本仓的 `Contract sync` workflow 收到后
自动刷新并开一个 PR（`.github/workflows/contract-sync.yml` + `scripts/ci/contract-sync-pr.sh`）。
**PR 需要人工 review 后才合并**，机器人不会自动合并；同一份契约重复触发不会产生空提交。
（本仓目前没有契约生成物；将来引入生成物后，必须把它一起绑进刷新命令 —— 生成物头部通常
嵌有快照的 sha256，只换快照会让一致性门禁变红。）

需要人工刷新时（例如通知没收到、或要指定某个上游 commit）：

```bash
scripts/ci/refresh-contract.sh

# 精确取某个上游 commit 的字节：
PIM_CONTRACT_REF=<40 位 SHA> scripts/ci/refresh-contract.sh
```

这是**唯一的一键刷新入口**：它先跑 `fetch-contract.sh` 拉快照与 provenance，再跑
`generate-contract-csharp.sh` 重新生成客户端产物，最后自我复核一次。**快照与生成物必须同批提交**
（生成物里嵌有快照的 sha256，只改一个会让 `contract-generated` 门禁变红），并在 PR 里说明刷新原因。

只改了 `scripts/contract/client-operations.json`（新增客户端要用的 operation）时，跑
`scripts/ci/generate-contract-csharp.sh` 即可。要换来源/ref 时有 env 可覆盖（默认即上游 `master`）：
`PIM_CONTRACT_REPO`、`PIM_CONTRACT_REF`（分支 / tag / 40 位 SHA）、`PIM_CONTRACT_RAW_BASE`、
`PIM_CONTRACT_REMOTE`、`PIM_CONTRACT_SOURCE_PATH`。

## 注意

- **快照落后会让本仓 CI 变红（预期行为）**：`contract-freshness` job 做上述逐字节比较，
  并已接进必过的 `CI Gate`。上游契约一前进而本仓没刷新 → `CI Gate` 红；
  跑一次 `scripts/ci/refresh-contract.sh` 并提交快照 + 生成物即恢复。
- **契约变了但生成物没重生成同样会红（预期行为）**：`contract-generated` job 重新生成后逐字节比对，
  并已接进必过的 `CI Gate`。
- 取不到权威源（网络不可达 / 404 / 空体 / 非合法 OpenAPI 3.x）**一律按失败处理**，
  不会跳过、不会当成「最新」。
- 上游出现**非契约提交**、契约字节没变 → 不红；`provenance.resolvedRef` **允许滞后**
  于上游 HEAD（它记录刷新当时的来源 SHA，不参与新鲜度判定）。
- 上游契约一变，三仓（web / android / windows）的 freshness job 会**同时变红**，
  需要人工逐个刷新并各开 PR —— 这是预期的人工确认点，不是 CI 故障。
- 接口边界的类型/路径/字段现在**编译期就有约束**：路径只能取自生成的 `ContractOperation`
  （构造函数私有），请求/响应类型来自生成的 DTO，契约 required 字段漏填是编译错误（CS9035）。
  能绕过去的只剩「改生成物」—— 那会被 `contract-generated` 门禁抓住。
- **服务基础地址**：`ApiClient` 的 `BaseAddress` 是 `<serverUrl>/api/v1/`，实际请求路径是生成的
  `ContractOperation.RelativePath`（契约路径去掉 `/api/v1/` 前缀），因此换服务器时子路径语义与改造前一致。
- 本机服务（keystats `/api/stats/`、browser bridge `/browser/*`）**不在**本契约范围内，它们的读写
  模型仍是 `Models/` 下的手写类型。

## 手写模型一致性报告（历史快照刷新产物）

下面两份报告是**本次接口边界契约化之前**、刷新快照时对手写 DTO 做的静态差异核对，
保留作为历史记录（报告里的快照哈希与 `models/` 文件名都是当时的状态，已不再对应当前代码
—— 手写的接口 DTO 已被生成物取代）：

- [`model-consistency.md`](model-consistency.md) —— 人读报告（13 个真实 operation 判定、security、覆盖口径、未验证项）
- [`model-consistency.detail.json`](model-consistency.detail.json) —— 机读详表（每条 finding 带
  `contractField.{required,readOnly,writeOnly,nullable,default,enum,ref,type}` 与 `requiredSemantics`，
  另有 `operationAudit` / `security` / `coverage` / `excludedFindings`）

注意：`required` 不区分方向；契约 schema 里 `readOnly: true` 且 `required` 的属性按 OpenAPI 3.0
**只对响应生效**，客户端不发它可能是正确行为，不能据此判定客户端 bug。
