using System;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 工单 06 / G1（首轮独立 review 的 Critical）：守护进程心跳与端点心跳共用同一个 2 分钟循环，
/// 但两条通道必须**互相隔离** —— 守护进程心跳 401/500 抛出的异常绝不能把端点心跳一起跳过。
///
/// <para>
/// 这不是假想的边界：本工单的原始故障形态就是"接口早就存在，但没有任何客户端在生产路径上调过它"。
/// 把"一拍"抽成 net8.0 里的 <see cref="HeartbeatTickRunner"/> 之后，这条控制流就能在 Linux 上被真实执行验证，
/// 而不是靠读 WPF 宿主的源码文本。
/// </para>
/// </summary>
public class HeartbeatTickRunnerTests
{
    [Fact]
    public async Task DaemonFailureDoesNotSkipTheEndpointHeartbeat()
    {
        var endpointCalls = 0;

        var result = await HeartbeatTickRunner.RunAsync(
            _ => throw new HttpRequestException("Response status code does not indicate success: 500"),
            _ =>
            {
                endpointCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(1, endpointCalls);
        Assert.False(result.DaemonHeartbeatSucceeded);
        Assert.Contains("500", result.DaemonHeartbeatError!);
        Assert.True(result.EndpointHeartbeatSucceeded);
        Assert.Null(result.EndpointHeartbeatError);
    }

    [Fact]
    public async Task EndpointFailureDoesNotHideTheDaemonResult()
    {
        var result = await HeartbeatTickRunner.RunAsync(
            _ => Task.CompletedTask,
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(result.DaemonHeartbeatSucceeded);
        Assert.False(result.EndpointHeartbeatSucceeded);
        Assert.Contains("boom", result.EndpointHeartbeatError!);
    }

    /// <summary>reporter 的失败是"返回 false + 自己记账"，不是抛异常：不能因此把结果写成"抛错"。</summary>
    [Fact]
    public async Task EndpointReportThatReturnsFalseIsNotAnUnexpectedError()
    {
        var result = await HeartbeatTickRunner.RunAsync(
            _ => Task.CompletedTask,
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.False(result.EndpointHeartbeatSucceeded);
        Assert.Null(result.EndpointHeartbeatError);
    }

    [Fact]
    public async Task BothSucceed()
    {
        var result = await HeartbeatTickRunner.RunAsync(
            _ => Task.CompletedTask,
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.True(result.DaemonHeartbeatSucceeded);
        Assert.True(result.EndpointHeartbeatSucceeded);
        Assert.Null(result.EndpointHeartbeatError);
    }

    /// <summary>主动取消必须照常向外抛（否则关停时会变成"一次正常的心跳失败"）。</summary>
    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var endpointCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeartbeatTickRunner.RunAsync(
            ct => Task.FromException(new OperationCanceledException(ct)),
            _ =>
            {
                endpointCalls++;
                return Task.FromResult(true);
            },
            cts.Token));

        Assert.Equal(0, endpointCalls);
    }
}
