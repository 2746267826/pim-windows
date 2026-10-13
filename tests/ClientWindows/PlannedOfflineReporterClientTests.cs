using System;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / REQ-COM-06（F-10）：从**不被编译的孤儿文件**
/// <c>PlannedOfflineReporterTests.cs</c> 迁移出来的 1 条**只依赖客户端类型**的用例。
///
/// 另一条 <c>BuildRequest_RoundTripsToServerDto</c> 需要后端程序集
/// <c>Pim.Core.Operations.PlannedOfflineRequest</c>，**不可移植**；归属与真实验证条件见
/// <c>tests/ClientWindows/BACKEND-OWNED-CASES.md</c>（保持 NOT-VERIFIED）。
/// 该上报体的字段/类型契约由 <c>ContractWireShapeTests</c> 的真实线上字节用例覆盖
/// （生产入口 → 捕获字节 → 对照 <c>contract/openapi.json</c>）。
/// </summary>
public sealed class PlannedOfflineReporterClientTests
{
    [Fact]
    public void BuildRequest_FillsDeviceKindAndReason()
    {
        var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var req = PlannedOfflineReporter.BuildRequest(Environment.MachineName, "shutdown", at);
        Assert.Equal(Environment.MachineName, req.DeviceId);
        Assert.Equal("windows", req.DaemonKind);
        Assert.Equal("shutdown", req.Reason);
        Assert.Equal(at, req.OccurredAt);
    }
}
