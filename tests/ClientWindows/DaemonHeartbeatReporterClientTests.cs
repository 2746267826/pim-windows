using System;
using System.Text.Json;
using Pim.Client.Core;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / REQ-COM-06（F-10）：从**不被编译的孤儿文件**
/// <c>DaemonHeartbeatReporterTests.cs</c> 迁移出来的 4 条**只依赖客户端类型**的用例
/// （BuildHeartbeat 的默认值/状态字段/statusDetails → StatusJson 内容）。
///
/// 第 5 条 <c>ClientHeartbeatJson_DeserializesIntoApiRequestContract</c> 需要后端程序集
/// <c>Pim.Core.Operations.DaemonHeartbeatRequest</c>，**不可移植**；其归属与真实验证条件
/// 见 <c>tests/ClientWindows/BACKEND-OWNED-CASES.md</c>（保持 NOT-VERIFIED）。
/// 心跳请求体的字段/类型契约由 <c>ContractWireShapeTests</c> 的真实现场字节用例覆盖。
/// </summary>
public sealed class DaemonHeartbeatReporterClientTests
{
    [Fact]
    public void BuildHeartbeat_UsesIpv4LoopbackDefaultServerUrl()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            ClientDefaults.DefaultServerUrl,
            DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            null,
            null);

        Assert.Equal("http://127.0.0.1:5858", heartbeat.ServerUrl);
        Assert.Equal("windows", heartbeat.DaemonKind);
        Assert.Equal("device-1", heartbeat.DeviceId);
    }

    [Fact]
    public void BuildHeartbeat_UsesDefaultServerUrlWhenBlank()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            " ",
            null,
            null,
            null);

        Assert.Equal("http://127.0.0.1:5858", heartbeat.ServerUrl);
    }

    [Fact]
    public void BuildHeartbeat_StatusJsonIncludesMachineAndProcess()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            ClientDefaults.DefaultServerUrl,
            null,
            null,
            null);

        using var status = JsonDocument.Parse(heartbeat.StatusJson);

        Assert.Equal(Environment.MachineName, status.RootElement.GetProperty("machine").GetString());
        Assert.Equal("pim-windows-daemon", status.RootElement.GetProperty("process").GetString());
    }

    [Fact]
    public void BuildHeartbeat_UsesProvidedSourceStatesAndStatusDetails()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            deviceId: "device-1",
            version: "1.0.0",
            serverUrl: ClientDefaults.DefaultServerUrl,
            lastSuccessfulUploadAt: DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            lastAttemptedUploadAt: DateTimeOffset.Parse("2026-05-24T00:01:00Z"),
            lastError: null,
            uploadQueueCount: 3,
            activityWatchState: "Available",
            keyStatsState: "Unavailable",
            statusDetails: new
            {
                keyStatsDetailState = "ApiOkButStaleZero",
                keyStatsProcessCount = 2,
                keyStatsSkipReason = "stale-zero",
                awQueueCount = 3
            });

        Assert.Equal("Available", heartbeat.ActivityWatchState);
        Assert.Equal("Unavailable", heartbeat.KeyStatsState);
        Assert.Equal(3, heartbeat.UploadQueueCount);

        using var status = JsonDocument.Parse(heartbeat.StatusJson);
        Assert.Equal("ApiOkButStaleZero", status.RootElement.GetProperty("keyStatsDetailState").GetString());
        Assert.Equal(2, status.RootElement.GetProperty("keyStatsProcessCount").GetInt32());
        Assert.Equal("stale-zero", status.RootElement.GetProperty("keyStatsSkipReason").GetString());
    }
}
