# F-10 · 后端依赖用例的归属与真实验证条件（runbook）

本文件是审计 **F-10**（`tests/ClientWindows` 里不被编译、不被执行的孤儿用例）在 round5 的
处置记录。round4 独立 review（`remediation/reviews/windows-round4-deepseek.md` I-1）确认：

- 孤儿分母 = **16 条用例**，按**用例级**归属拆分，不是按文件级；
- **可移植 10 条**（只依赖客户端类型）→ round5 已迁入参与编译的测试工程并真实执行；
- **后端依赖 6 条**（引用 `pim-api` 侧程序集）→ 本仓**无法编译**，因此**不得**伪造成客户端验证，
  状态保持 `NOT-VERIFIED`，逐条登记归属与真实验证条件（即本文件）。

原 4 个孤儿文件（`BusinessDayUtilsTests.cs`、`DaemonHeartbeatReporterTests.cs`、
`PlannedOfflineReporterTests.cs`、`Wo398SuspendGapTests.cs`）已在 round5 删除：
可移植用例迁入下方「迁移去向」，后端用例的正文原样保留在本文件中供 `pim-api` 侧落地。

## 1. 迁移去向（10 条可移植用例，均参与编译与执行）

| 原用例 | 迁移去向（编译中的文件） |
| --- | --- |
| `BusinessDayUtilsTests.GetBusinessDate_BeforeFourAmShanghai_BelongsToPreviousCalendarDay` | `BusinessDayUtilsClientTests.cs` |
| `BusinessDayUtilsTests.GetBusinessDate_AtOrAfterFourAmShanghai_BelongsToCurrentCalendarDay` | `BusinessDayUtilsClientTests.cs` |
| `BusinessDayUtilsTests.GetNextBusinessDayStart_ReturnsNextFourAmShanghaiInUtc` | `BusinessDayUtilsClientTests.cs` |
| `DaemonHeartbeatReporterTests.BuildHeartbeat_UsesIpv4LoopbackDefaultServerUrl` | `DaemonHeartbeatReporterClientTests.cs` |
| `DaemonHeartbeatReporterTests.BuildHeartbeat_UsesDefaultServerUrlWhenBlank` | `DaemonHeartbeatReporterClientTests.cs` |
| `DaemonHeartbeatReporterTests.BuildHeartbeat_StatusJsonIncludesMachineAndProcess` | `DaemonHeartbeatReporterClientTests.cs` |
| `DaemonHeartbeatReporterTests.BuildHeartbeat_UsesProvidedSourceStatesAndStatusDetails` | `DaemonHeartbeatReporterClientTests.cs` |
| `PlannedOfflineReporterTests.BuildRequest_FillsDeviceKindAndReason` | `PlannedOfflineReporterClientTests.cs` |
| `Wo398SuspendGapTests.Ac4_4_AliveButQuietFor40Minutes_WithoutDeclaration_EmitsGapChunksForTheWholeInterval`（原行 66） | `SuspendGapClientTests.cs` |
| `Wo398SuspendGapTests.Ac4_4_SuspendThenResume_CoversTheWholeSleepIntervalWithGapChunks`（原行 93） | `SuspendGapClientTests.cs` |

用例名与断言逐字保留（只去掉对后端程序集的 `using`），因此审计分母可逐条对照。
这 10 条由 `Pim.Client.Tests`（net8.0，CI `test-client-core` job）真实编译并执行；
`scripts/ci/test-orphan-test-coverage.mjs` 会对「方法声明 + `[Fact]` 属性 + 参与编译」做机械检查。

## 2. 后端依赖 6 条：归属与真实验证条件（全部 `NOT-VERIFIED`）

归属仓 = **`pim-api`**（本仓不可能提供 `Pim.Core` / `Pim.Core.Operations` /
`Pim.Core.Invariants` / `Pim.Module.PcTracker` 这些程序集）。

| # | 原用例 | 依赖的后端类型 | 真实验证条件（必须满足其一，且要有真实宿主） |
| --- | --- | --- | --- |
| B-1 | `DaemonHeartbeatReporterTests.ClientHeartbeatJson_DeserializesIntoApiRequestContract` | `Pim.Core.Operations.DaemonHeartbeatRequest`、`Pim.Core.Operations.DaemonSourceState` | 在**引用 `Pim.Core.Operations` 的宿主**里跑：客户端序列化的 JSON → `System.Text.Json` 反序列化成服务端 DTO，并断言 `ActivityWatchState` / `KeyStatsState` / `UploadQueueCount`。落地方式：`pim-api` 以 NuGet 契约包提供这些程序集，或把该用例放进 `pim-api` 侧 CI 工程。 |
| B-2 | `PlannedOfflineReporterTests.BuildRequest_RoundTripsToServerDto` | `Pim.Core.Operations.PlannedOfflineRequest` | 同上宿主条件下，断言 `deviceId` / `daemonKind` / `reason` / `occurredAt` 往返一致。 |
| B-3 | `BusinessDayUtilsTests.PcTrackerService_GetBusinessDayForTimestamp_MatchesBusinessDayUtils` | `Pim.Module.PcTracker.Services.PcTrackerService` | 在引用 `Pim.Module.PcTracker` 的宿主里，对同一批时间戳断言「服务端营业日 == 客户端 `BusinessDayUtils.GetBusinessDate`」——这是**跨端一致**判据，只能在后端侧跑。 |
| B-4 | `Wo398SuspendGapTests.Ac4_4_PointDeclaration_DoesNotMaskALaterRealGap`（原行 118） | `Pim.Core.Invariants.DataReliabilityInvariants.CheckS6_OfflineDeclared` | 在引用 `Pim.Core.Invariants` 的宿主里构造 `DeviceActivityTrace`/`OfflineDeclaration`，断言 7 秒小空档被声明覆盖、其后 40 分钟断档判红（`undeclared-gap` / `gapMinutes=40.0`）。 |
| B-5 | `Wo398SuspendGapTests.Ac4_4_SevenSecondRecoveryThenAGap_IsCoveredByTheExistingBoundedGrace`（原行 152） | 同上 | 断言 ±5 分钟有界宽限内 `Pass`、`TotalViolations==0`。 |
| B-6 | `Wo398SuspendGapTests.Ac4_4_DeclarationCoveringTheSleepInterval_MarksThatGapAsDeclared`（原行 184） | 同上 | 断言整段休眠区间被认成「已声明」：`Pass`、`WindowViolations==0`。 |

**不许**用来冒充完成的替代物：拿「可移植用例编译过了」当这 6 条 FIXED；拿目录名/文件存在当
coverage；在 `contract/openapi.json` 上手写后端 DTO；把这 6 条改写成只断客户端字符串。

### 2.1 B-4 / B-5 / B-6 的对照关系（本仓已有真实证据）

B-4/B-5/B-6 判的是**服务端** `DataReliabilityInvariants` 的语义；本仓能真实观测的是
**客户端**在同样情形下补齐/上报 gap 的行为，已由编译中的
`SuspendGapClientTests`（静默补齐、休眠补齐）+ `ContractWireShapeTests`（gap 分片字段与
`contract/openapi.json` 一致）覆盖。两者互补，**不能互相替代**：客户端补齐正确不等于服务端
判据正确，反之亦然。

## 3. 复现步骤（后端侧落地后）

1. 在 `pim-api` 侧建立引用 `Pim.Core` / `Pim.Module.PcTracker` 的测试工程；
2. 把第 2 节表格里对应用例的原正文粘回（下方 §4 有逐字正文）；
3. 客户端侧序列化输入的字段名以 `contract/openapi.json`（本仓快照）为准，禁止手改契约；
4. 完成后把本表 B-1…B-6 的状态从 `NOT-VERIFIED` 改成有证据的 `PASS`，并把证据位置写进
   `remediation/pim-windows/round*/report.md`——在此之前不得升格。

## 4. 原用例正文（逐字保留，供 `pim-api` 侧复现）

### B-1（`Pim.Core.Operations`）

```csharp
[Fact]
public void ClientHeartbeatJson_DeserializesIntoApiRequestContract()
{
    var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
        "device-1",
        "1.0.0",
        ClientDefaults.DefaultServerUrl,
        DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
        DateTimeOffset.Parse("2026-05-24T00:01:00Z"),
        null,
        uploadQueueCount: 1,
        activityWatchState: "Available",
        keyStatsState: "Unavailable");
    var json = JsonSerializer.Serialize(heartbeat, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    var request = JsonSerializer.Deserialize<ApiDaemonHeartbeatRequest>(
        json,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    Assert.NotNull(request);
    Assert.Equal(DaemonSourceState.Available, request.ActivityWatchState);
    Assert.Equal(DaemonSourceState.Unavailable, request.KeyStatsState);
    Assert.Equal(1, request.UploadQueueCount);
}
```

### B-2（`Pim.Core.Operations`）

```csharp
[Fact]
public void BuildRequest_RoundTripsToServerDto()
{
    var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    var client = PlannedOfflineReporter.BuildRequest("PC-1", "suspend", at);
    var json = JsonSerializer.Serialize(client, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var server = JsonSerializer.Deserialize<ServerPlannedOfflineRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Assert.NotNull(server);
    Assert.Equal("PC-1", server.DeviceId);
    Assert.Equal("windows", server.DaemonKind);
    Assert.Equal("suspend", server.Reason);
    Assert.Equal(at, server.OccurredAt);
}
```

### B-3（`Pim.Module.PcTracker`）

```csharp
[Fact]
public void PcTrackerService_GetBusinessDayForTimestamp_MatchesBusinessDayUtils()
{
    var testTimestamps = new[]
    {
        new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 24, 3, 59, 59, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 24, 23, 59, 59, TimeSpan.FromHours(8))
    };

    foreach (var ts in testTimestamps)
    {
        var clientDate = BusinessDayUtils.GetBusinessDate(ts);
        var serverDate = PcTrackerService.GetBusinessDayForTimestamp(ts);
        Assert.Equal(clientDate.ToDateTime(TimeOnly.MinValue), serverDate);
    }
}
```

### B-4 / B-5 / B-6（`Pim.Core.Invariants`）

```csharp
[Fact]
public void Ac4_4_PointDeclaration_DoesNotMaskALaterRealGap()
{
    var declarationAt = T0.UtcDateTime;
    var trace = new DeviceActivityTrace
    {
        DeviceId = "PC-01",
        EventIntervals = new List<(DateTime, DateTime)>
        {
            (declarationAt.AddMinutes(-30), declarationAt),
            (declarationAt.AddSeconds(7), declarationAt.AddMinutes(7)),
            (declarationAt.AddMinutes(47), declarationAt.AddMinutes(48))
        },
        Declarations = new List<OfflineDeclaration>
        {
            new() { DeviceId = "PC-01", StartTime = declarationAt, EndTime = declarationAt, Reason = "suspend" }
        },
        UploadLagSamples = Array.Empty<UploadLagSample>()
    };

    var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, referenceTimeUtc: declarationAt.AddDays(1));

    Assert.False(result.Pass);
    Assert.Equal(1, result.TotalViolations);
    var violation = Assert.Single(result.Violations);
    Assert.Equal("undeclared-gap", violation.Fields["kind"]);
    Assert.Equal("40.0", violation.Fields["gapMinutes"]);
}

[Fact]
public void Ac4_4_SevenSecondRecoveryThenAGap_IsCoveredByTheExistingBoundedGrace()
{
    var declarationAt = T0.UtcDateTime;
    var trace = new DeviceActivityTrace
    {
        DeviceId = "PC-01",
        EventIntervals = new List<(DateTime, DateTime)>
        {
            (declarationAt.AddMinutes(-30), declarationAt),
            (declarationAt.AddSeconds(7), declarationAt.AddSeconds(14)),
            (declarationAt.AddMinutes(40).AddSeconds(14), declarationAt.AddMinutes(41).AddSeconds(14))
        },
        Declarations = new List<OfflineDeclaration>
        {
            new() { DeviceId = "PC-01", StartTime = declarationAt, EndTime = declarationAt, Reason = "suspend" }
        }
    };

    var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, referenceTimeUtc: declarationAt.AddDays(1));

    Assert.True(result.Pass, result.Detail);
    Assert.Equal(0, result.TotalViolations);
}

[Fact]
public void Ac4_4_DeclarationCoveringTheSleepInterval_MarksThatGapAsDeclared()
{
    var suspendAt = T0.UtcDateTime;
    var trace = new DeviceActivityTrace
    {
        DeviceId = "PC-01",
        EventIntervals = new List<(DateTime, DateTime)>
        {
            (suspendAt.AddMinutes(-30), suspendAt),
            (suspendAt.AddMinutes(42), suspendAt.AddMinutes(72))
        },
        Declarations = new List<OfflineDeclaration>
        {
            new() { DeviceId = "PC-01", StartTime = suspendAt, EndTime = suspendAt, Reason = "suspend" }
        }
    };

    var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, referenceTimeUtc: suspendAt.AddDays(1));

    Assert.True(result.Pass, result.Detail);
    Assert.Equal(0, result.WindowViolations);
}
```

（`T0 = new DateTimeOffset(2026, 10, 7, 5, 0, 0, TimeSpan.Zero)`。）
