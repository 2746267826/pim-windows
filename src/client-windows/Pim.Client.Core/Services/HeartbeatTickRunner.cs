namespace Pim.Client.Core.Services;

/// <summary>
/// 守护程序心跳"一拍"的结果：两条上报通道各自的结果。
/// <para>
/// 存在的理由是**隔离**：守护进程心跳（<c>/api/v1/daemon/heartbeat</c>）与端点心跳
/// （<c>/api/v1/endpoints/{deviceId}/heartbeat</c>）都在这同一个 2 分钟循环里，
/// 前者 401/500 时抛出的异常绝不能把后者一起跳过 —— 那正是本工单要修的故障形态
/// （"接口早就存在，但生产路径根本走不到"）。
/// </para>
/// </summary>
public sealed record HeartbeatTickResult(
    string? DaemonHeartbeatError,
    bool EndpointHeartbeatSucceeded,
    string? EndpointHeartbeatError)
{
    public bool DaemonHeartbeatSucceeded => DaemonHeartbeatError is null;
}

/// <summary>
/// 心跳"一拍"的执行器：依次跑两条上报，**各自隔离异常**。
///
/// <para>
/// 只有调用方主动取消时才向外抛 <see cref="OperationCanceledException"/>；
/// 其它异常都被记进 <see cref="HeartbeatTickResult"/>，由调用方决定怎么留痕（不静默吞掉）。
/// 放在 Core（net8.0）而不是 WPF 宿主里，是为了让"一条失败不影响另一条"这件事能在 Linux 上被真实执行验证。
/// </para>
/// </summary>
public static class HeartbeatTickRunner
{
    public static async Task<HeartbeatTickResult> RunAsync(
        Func<CancellationToken, Task> reportDaemonHeartbeat,
        Func<CancellationToken, Task<bool>> reportEndpointHeartbeat,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reportDaemonHeartbeat);
        ArgumentNullException.ThrowIfNull(reportEndpointHeartbeat);

        string? daemonError = null;
        try
        {
            await reportDaemonHeartbeat(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            daemonError = Describe(ex);
        }

        var endpointOk = false;
        string? endpointError = null;
        try
        {
            endpointOk = await reportEndpointHeartbeat(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            endpointError = Describe(ex);
        }

        return new HeartbeatTickResult(daemonError, endpointOk, endpointError);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
