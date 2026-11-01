using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

/// <summary>
/// Real Windows child-process evidence for issue #516 phase 4 lifecycle. The helper
/// is synthetic; no inference, download or production access is involved.
/// </summary>
public class WindowsControlledProcessRunnerTests
{
    private static readonly string HelperPath = Path.Combine(
        AppContext.BaseDirectory,
        "helper",
        "Pim.LocalRuntime.Helper.exe");

    [Fact]
    public async Task Success_CapturesStdoutStderrAndExitCode()
    {
        var result = await RunAsync(new[] { "--mode", "ok" }, TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded, result.ErrorCode + " " + result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("HELPER_OK", result.StandardOutput);
        Assert.Contains("HELPER_STDERR", result.StandardError);
    }

    [Fact]
    public async Task DeepVerifiedExecutablePath_FailsClosedWithoutExplicitCwdFallbackOptIn()
    {
        await WithDeepHelperAsync(async (root, executable) =>
        {
            var result = await new ControlledProcessRunner().StartAsync(
                new RuntimeLaunchSpec(
                    executable,
                    new[] { "--mode", "ok" },
                    root,
                    Sha256(executable),
                    TimeSpan.FromSeconds(30)));

            Assert.False(result.Succeeded);
            Assert.Equal(LocalRuntimeReasonCodes.ProcessStartFailed, result.ErrorCode);
            Assert.Contains("create-process-267", result.StandardError);
        });
    }

    [Fact]
    public async Task DeepVerifiedExecutablePath_LaunchesOnlyWithExplicitCwdFallbackOptIn()
    {
        await WithDeepHelperAsync(async (root, executable) =>
        {
            var result = await new ControlledProcessRunner().StartAsync(
                new RuntimeLaunchSpec(
                    executable,
                    new[] { "--mode", "ok" },
                    root,
                    Sha256(executable),
                    TimeSpan.FromSeconds(30),
                    AllowCwdFallbackWhenInvalid: true));

            Assert.True(result.Succeeded, result.ErrorCode + " " + result.StandardError);
            Assert.Contains("HELPER_OK", result.StandardOutput);
        });
    }

    [Fact]
    public async Task NonZeroExit_IsReportedAsFailure()
    {
        var result = await RunAsync(new[] { "--mode", "fail", "--code", "7" }, TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("HELPER_FAIL", result.StandardError);
    }

    [Fact]
    public async Task Cancel_TerminatesProcessAndReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var task = RunAsync(new[] { "--mode", "sleep", "--ms", "60000" }, TimeSpan.FromMinutes(2), cancellation.Token);
        await Task.Delay(700);
        cancellation.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Cancelled);
        Assert.Equal(LocalRuntimeReasonCodes.ProcessCancelled, result.ErrorCode);
    }

    [Fact]
    public async Task Timeout_TerminatesProcessAndReportsTimedOut()
    {
        var result = await RunAsync(
            new[] { "--mode", "sleep", "--ms", "60000" },
            TimeSpan.FromMilliseconds(800));

        Assert.True(result.TimedOut);
        Assert.Equal(LocalRuntimeReasonCodes.ProcessTimedOut, result.ErrorCode);
    }

    [Fact]
    public async Task Output_IsBoundedAndTruncated()
    {
        var result = await RunAsync(
            new[] { "--mode", "flood", "--bytes", "200000" },
            TimeSpan.FromSeconds(30),
            maxOutputBytes: 4096);

        Assert.False(result.Succeeded);
        Assert.True(result.OutputTruncated);
        Assert.Equal(LocalRuntimeReasonCodes.OutputTruncated, result.ErrorCode);
        Assert.Contains("[output truncated]", result.StandardOutput);
        Assert.InRange(result.StandardOutput.Length, 4096, 4200);
    }

    [Fact]
    public async Task ImmediateChild_IsInsideJob_AndKilledOnCancel()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "pim-child-" + Guid.NewGuid().ToString("N") + ".pid");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var task = RunAsync(
                new[] { "--mode", "spawn-child", "--ms", "60000", "--child-pid-file", pidFile },
                TimeSpan.FromMinutes(2),
                cancellation.Token);

            var childPid = await WaitForChildPidAsync(pidFile);
            Assert.True(IsAlive(childPid), "child should be running before cancel");

            cancellation.Cancel();
            var result = await task.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(result.Cancelled);
            await AssertEventuallyExitedAsync(childPid);
        }
        finally
        {
            TryDelete(pidFile);
        }
    }

    [Fact]
    public async Task RootExit_WithLiveChild_ChildIsReapedAndPipesDrain()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "pim-child-" + Guid.NewGuid().ToString("N") + ".pid");
        try
        {
            var started = Stopwatch.StartNew();
            var result = await RunAsync(
                new[] { "--mode", "spawn-child-exit", "--ms", "60000", "--child-pid-file", pidFile },
                TimeSpan.FromSeconds(30));
            started.Stop();

            var childPid = int.Parse(await File.ReadAllTextAsync(pidFile));
            Assert.True(result.Succeeded, result.ErrorCode + " " + result.StandardError);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), $"run took {started.Elapsed}");
            await AssertEventuallyExitedAsync(childPid);
        }
        finally
        {
            TryDelete(pidFile);
        }
    }

    [Fact]
    public async Task Registry_IsEmptyAfterProvenExit()
    {
        var registryPath = Path.Combine(Path.GetTempPath(), "pim-registry-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var registry = new OwnedProcessRegistry(registryPath);
            var runner = new ControlledProcessRunner(registry);
            var result = await runner.StartAsync(new RuntimeLaunchSpec(
                HelperPath,
                new[] { "--mode", "ok" },
                Path.GetDirectoryName(HelperPath)!,
                Sha256(HelperPath),
                TimeSpan.FromSeconds(30)));

            Assert.True(result.Succeeded, result.ErrorCode);
            Assert.Empty(registry.Snapshot());
        }
        finally
        {
            TryDelete(registryPath);
        }
    }

    [Fact]
    public void OwnedGuard_DoesNotKillWhenIdentityDoesNotMatch()
    {
        using var current = Process.GetCurrentProcess();
        var wrong = new OwnedProcessIdentity(
            current.Id,
            current.StartTime.ToUniversalTime().AddSeconds(-5).Ticks,
            0,
            current.MainModule?.FileName ?? "unknown");

        var result = OwnedProcessGuard.TryKillOwned(wrong);

        Assert.False(result.Killed);
        Assert.False(result.IdentityVerified);
        Assert.False(current.HasExited);
    }

    private static Task<RuntimeLaunchResult> RunAsync(
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int maxOutputBytes = 64 * 1024)
    {
        var runner = new ControlledProcessRunner();
        return runner.StartAsync(
            new RuntimeLaunchSpec(
                HelperPath,
                arguments,
                Path.GetDirectoryName(HelperPath)!,
                Sha256(HelperPath),
                timeout,
                maxOutputBytes),
            cancellationToken);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task WithDeepHelperAsync(Func<string, string, Task> action)
    {
        var cleanupRoot = Path.Combine(
            Path.GetTempPath(),
            "pim-deep-runner-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(
            cleanupRoot,
            new string('a', 120),
            new string('b', 100));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var source in Directory.EnumerateFiles(Path.GetDirectoryName(HelperPath)!))
            {
                File.Copy(source, Path.Combine(root, Path.GetFileName(source)));
            }

            var executable = Path.Combine(root, Path.GetFileName(HelperPath));
            Assert.True(executable.Length > 248);
            await action(root, executable);
        }
        finally
        {
            TryDeleteDirectory(cleanupRoot);
        }
    }

    private static async Task<int> WaitForChildPidAsync(string pidFile)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    return int.Parse(await File.ReadAllTextAsync(pidFile));
                }
                catch (IOException)
                {
                    // file still being written
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("helper did not write child pid");
    }

    private static async Task AssertEventuallyExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!IsAlive(processId))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"child process {processId} is still running");
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
