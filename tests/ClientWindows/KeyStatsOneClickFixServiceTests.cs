using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsOneClickFixServiceTests
{
    private const int SessionId = 1;

    [Fact]
    public async Task RunAsync_NoElevation_WhenStopsSucceedAndNoForeign()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true),
            new(200, SessionId, true)
        };

        var elevateCalls = 0;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids =>
            {
                var results = ids.Select(id =>
                {
                    processes.RemoveAll(p => p.ProcessId == id);
                    return new KeyStatsStopResult(id, Succeeded: true, Error: null);
                }).ToArray();
                return results;
            },
            start: _ =>
            {
                if (!processes.Any(p => p.IsCurrentUserSession && p.SessionId == SessionId))
                    processes.Add(new KeyStatsProcessInfo(300, SessionId, true));
            },
            elevate: (_, _, _) =>
            {
                elevateCalls++;
                return Task.FromResult((0, "", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(KeyStatsFixOutcome.Succeeded, result.Outcome);
        Assert.False(result.ElevatedUsed);
        Assert.Equal(0, elevateCalls);
        Assert.True(result.ApiReachable);
        Assert.True(result.CountersGrew);
        Assert.Contains(200, result.StoppedProcessIds);
    }

    [Fact]
    public async Task RunAsync_RequestsElevation_WhenAccessDenied()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true),
            new(200, 0, false)
        };

        var elevated = false;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
            {
                if (id == 200)
                    return new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied");
                processes.RemoveAll(p => p.ProcessId == id);
                return new KeyStatsStopResult(id, Succeeded: true, Error: null);
            }).ToArray(),
            start: _ =>
            {
                if (!processes.Any(p => p.IsCurrentUserSession && p.SessionId == SessionId))
                    processes.Add(new KeyStatsProcessInfo(300, SessionId, true));
            },
            elevate: (_, _, _) =>
            {
                elevated = true;
                processes.RemoveAll(p => p.ProcessId == 200);
                return Task.FromResult((0, "cleaned", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.True(elevated);
        Assert.True(result.ElevatedUsed);
        Assert.Equal(0, result.ScriptExitCode);
        Assert.Equal(KeyStatsFixOutcome.Succeeded, result.Outcome);
        Assert.Contains(200, result.FailedStopProcessIds);
    }

    [Fact]
    public async Task RunAsync_NoElevation_WhenAccessDeniedButFailedPidGoneAndNoForeign()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true),
            new(200, SessionId, true)
        };

        var elevateCalls = 0;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
            {
                // Report access-denied, but process is already gone on re-list.
                processes.RemoveAll(p => p.ProcessId == id);
                return new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied");
            }).ToArray(),
            start: _ =>
            {
                if (!processes.Any(p => p.IsCurrentUserSession && p.SessionId == SessionId))
                    processes.Add(new KeyStatsProcessInfo(300, SessionId, true));
            },
            elevate: (_, _, _) =>
            {
                elevateCalls++;
                return Task.FromResult((0, "", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(0, elevateCalls);
        Assert.False(result.ElevatedUsed);
        Assert.Equal(KeyStatsFixOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_PassesSharedLogPath_ToElevate()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(200, 0, false)
        };

        string? receivedLogPath = null;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ =>
            {
                if (!processes.Any(p => p.IsCurrentUserSession && p.SessionId == SessionId))
                    processes.Add(new KeyStatsProcessInfo(300, SessionId, true));
            },
            elevate: (_, _, logPath) =>
            {
                receivedLogPath = logPath;
                processes.RemoveAll(p => p.ProcessId == 200);
                return Task.FromResult((0, "cleaned", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.True(result.ElevatedUsed);
        Assert.NotNull(receivedLogPath);
        Assert.EndsWith(KeyStatsOneClickFixService.FixLogFileName, receivedLogPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PIM", receivedLogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_Cancelled_WhenUserRejectsConfirm()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(200, 0, false)
        };

        var elevateCalls = 0;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ => { },
            elevate: (_, _, _) =>
            {
                elevateCalls++;
                return Task.FromResult((0, "", false));
            },
            snapshots: ZeroSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => false);

        Assert.Equal(KeyStatsFixOutcome.Cancelled, result.Outcome);
        Assert.False(result.ElevatedUsed);
        Assert.Equal(0, elevateCalls);
    }

    [Fact]
    public async Task RunAsync_Partial_WhenApiOkButCountersStillZero()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true)
        };

        var (service, exe, script) = CreateService(
            processes,
            stop: _ => Array.Empty<KeyStatsStopResult>(),
            start: _ => { },
            elevate: (_, _, _) => Task.FromResult((0, "", false)),
            snapshots: ZeroSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(KeyStatsFixOutcome.Partial, result.Outcome);
        Assert.True(result.ApiReachable);
        Assert.False(result.CountersGrew);
        Assert.True(
            result.Phase2MessageZh.Contains("键盘", StringComparison.Ordinal) ||
            result.Phase2MessageZh.Contains("刷新", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Failed_WhenExeMissing()
    {
        var (service, _, script) = CreateService(
            new List<KeyStatsProcessInfo>(),
            stop: _ => Array.Empty<KeyStatsStopResult>(),
            start: _ => { },
            elevate: (_, _, _) => Task.FromResult((0, "", false)),
            snapshots: ZeroSnapshots(),
            createExe: false);

        var result = await service.RunAsync(
            Path.Combine(Path.GetTempPath(), $"missing-keystats-{Guid.NewGuid():N}.exe"),
            script,
            SessionId,
            _ => true);

        Assert.Equal(KeyStatsFixOutcome.Failed, result.Outcome);
        Assert.Contains("未找到 KeyStats.exe", result.Phase1MessageZh);
    }

    [Fact]
    public async Task RunAsync_Failed_WhenElevationNeededButScriptMissing()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(200, 0, false)
        };

        var (service, exe, _) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ => { },
            elevate: (_, _, _) => Task.FromResult((0, "", false)),
            snapshots: ZeroSnapshots(),
            createScript: false);

        var result = await service.RunAsync(
            exe,
            Path.Combine(Path.GetTempPath(), $"missing-fix-{Guid.NewGuid():N}.ps1"),
            SessionId,
            _ => true);

        Assert.Equal(KeyStatsFixOutcome.Failed, result.Outcome);
        Assert.Contains("脚本", result.Phase1MessageZh);
        Assert.False(result.ElevatedUsed);
    }

    [Fact]
    public async Task RunAsync_Failed_WhenElevateExitCodeNonZero()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(200, 0, false)
        };

        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ => { },
            elevate: (_, _, _) => Task.FromResult((7, "script error detail", false)),
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(KeyStatsFixOutcome.Failed, result.Outcome);
        Assert.True(result.ElevatedUsed);
        Assert.Equal(7, result.ScriptExitCode);
        Assert.Contains("7", result.Phase1MessageZh);
    }

    [Fact]
    public async Task RunAsync_Cancelled_WhenElevateReturnsCancelled()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(200, 0, false)
        };

        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ => { },
            elevate: (_, _, _) => Task.FromResult((0, "", true)),
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(KeyStatsFixOutcome.Cancelled, result.Outcome);
        Assert.False(result.ElevatedUsed);
        Assert.Contains("UAC", result.Phase1MessageZh);
    }

    [Fact]
    public async Task RunAsync_RequestsElevation_WhenStopSucceedsButForeignRemains()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true),
            new(200, 0, false)
        };

        var elevateCalls = 0;
        var startCalls = 0;
        var elevatedSeen = false;
        var startBeforeElevate = false;
        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
            {
                // Report success for foreign stop, but leave it listed (reappears / not really gone).
                if (id != 200)
                    processes.RemoveAll(p => p.ProcessId == id);
                return new KeyStatsStopResult(id, Succeeded: true, Error: null);
            }).ToArray(),
            start: _ =>
            {
                startCalls++;
                if (!elevatedSeen)
                    startBeforeElevate = true;
                if (!processes.Any(p => p.IsCurrentUserSession && p.SessionId == SessionId))
                    processes.Add(new KeyStatsProcessInfo(300, SessionId, true));
            },
            elevate: (_, _, _) =>
            {
                elevateCalls++;
                elevatedSeen = true;
                processes.RemoveAll(p => p.ProcessId == 200);
                return Task.FromResult((0, "cleaned foreign", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(1, elevateCalls);
        Assert.True(result.ElevatedUsed);
        Assert.False(startBeforeElevate);
        Assert.Equal(1, startCalls);
        Assert.Equal(KeyStatsFixOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_FailedMessage_WhenCountersGrewButProcessNotOk()
    {
        var processes = new List<KeyStatsProcessInfo>
        {
            new(100, SessionId, true),
            new(200, 0, false)
        };

        var (service, exe, script) = CreateService(
            processes,
            stop: ids => ids.Select(id =>
                new KeyStatsStopResult(id, Succeeded: false, Error: "access-denied")).ToArray(),
            start: _ => { },
            elevate: (_, _, _) =>
            {
                // Elevate "succeeds" but leaves foreign process in place.
                return Task.FromResult((0, "partial clean", false));
            },
            snapshots: GrowingSnapshots());

        var result = await service.RunAsync(exe, script, SessionId, _ => true);

        Assert.Equal(KeyStatsFixOutcome.Failed, result.Outcome);
        Assert.True(result.CountersGrew);
        Assert.DoesNotContain("修复成功", result.Phase2MessageZh);
        Assert.True(
            result.Phase2MessageZh.Contains("进程", StringComparison.Ordinal) ||
            result.Phase2MessageZh.Contains("异常", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GrowingSnapshots() => new[]
    {
        """{"keyPresses":0,"leftClicks":0,"rightClicks":0,"middleClicks":0,"sideBackClicks":0,"sideForwardClicks":0,"mouseDistance":0,"scrollDistance":0}""",
        """{"keyPresses":5,"leftClicks":0,"rightClicks":0,"middleClicks":0,"sideBackClicks":0,"sideForwardClicks":0,"mouseDistance":0,"scrollDistance":0}"""
    };

    private static IReadOnlyList<string> ZeroSnapshots() => new[]
    {
        """{"keyPresses":0,"leftClicks":0,"rightClicks":0,"middleClicks":0,"sideBackClicks":0,"sideForwardClicks":0,"mouseDistance":0,"scrollDistance":0}""",
        """{"keyPresses":0,"leftClicks":0,"rightClicks":0,"middleClicks":0,"sideBackClicks":0,"sideForwardClicks":0,"mouseDistance":0,"scrollDistance":0}"""
    };

    private static (
        KeyStatsOneClickFixService Service,
        string ExePath,
        string ScriptPath) CreateService(
        List<KeyStatsProcessInfo> processes,
        Func<IReadOnlyList<int>, IReadOnlyList<KeyStatsStopResult>> stop,
        Action<string> start,
        Func<string, string, string, Task<(int ExitCode, string Output, bool Cancelled)>> elevate,
        IReadOnlyList<string> snapshots,
        bool createExe = true,
        bool createScript = true)
    {
        var dir = CreateIsolatedTestDir();
        var exe = Path.Combine(dir, "KeyStats.exe");
        var script = Path.Combine(dir, KeyStatsOneClickFixService.FixScriptFileName);
        if (createExe)
            File.WriteAllText(exe, "stub");
        if (createScript)
            File.WriteAllText(script, "# stub");

        var call = 0;
        var handler = new StubHandler(_ =>
        {
            var body = snapshots[Math.Min(call, snapshots.Count - 1)];
            call++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080") };
        var stats = new KeyStatsLocalStatsClient(http);
        var mgr = new KeyStatsProcessManager();

        var service = new KeyStatsOneClickFixService(
            mgr,
            stats,
            stop: stop,
            start: start,
            runElevatedScript: elevate,
            delayPhase1: _ => Task.CompletedTask,
            delayPhase2: _ => Task.CompletedTask,
            listProcesses: _ => processes.ToArray());

        return (service, exe, script);
    }

    /// <summary>
    /// 测试自有隔离目录（round4 · 环境修复）。
    ///
    /// 旧写法把固定目录 <c>{TMPDIR}/pim-keystats-fix-tests</c> 当父目录，父目录一旦被
    /// 别的用户以不可写权限预先创建，<c>Directory.CreateDirectory</c> 就抛
    /// <see cref="UnauthorizedAccessException"/>：在 <c>TMPDIR=/tmp</c> 的环境下
    /// 12 条用例会以环境错误失败（Grok round3 M-1 / round4 logs/02）。
    /// 这里改成「每次调用一个新的、由本测试进程拥有的目录」，首选带进程号的临时目录，
    /// 不可写时退回测试程序集输出目录。只换目录，不改任何断言。
    /// </summary>
    private static string CreateIsolatedTestDir()
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetTempPath(), $"pim-keystats-fix-tests-{Environment.ProcessId}"),
            Path.Combine(AppContext.BaseDirectory, "pim-keystats-fix-tests"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var dir = Path.Combine(candidate, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 换下一个候选目录；两个都不可写才抛出。
            }
        }

        throw new InvalidOperationException("无法创建隔离的 KeyStats 修复测试目录（临时目录与测试输出目录都不可写）");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
