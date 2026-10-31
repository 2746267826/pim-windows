using System.Diagnostics;
using System.Text;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public interface IControlledProcessRunner
{
    Task<RuntimeLaunchResult> StartAsync(
        RuntimeLaunchSpec spec,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a verified executable as a controlled child process. On Windows the child is
/// created suspended, assigned to a kill-on-close Job Object, and only then resumed,
/// so it cannot spawn a descendant before entering the job. Cancel, timeout, our own
/// crash (job handle close) and normal root exit all reap the job tree. The owned
/// process registry is kept as extra cleanup evidence until exit is proven.
/// </summary>
public sealed class ControlledProcessRunner : IControlledProcessRunner
{
    private const int MinimumOutputBudget = 1024;
    private const int MaximumOutputBudget = 4 * 1024 * 1024;
    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(5);

    private readonly OwnedProcessRegistry? _registry;

    public ControlledProcessRunner(OwnedProcessRegistry? registry = null)
    {
        _registry = registry;
    }

    public async Task<RuntimeLaunchResult> StartAsync(
        RuntimeLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var started = Stopwatch.GetTimestamp();

        if (!File.Exists(spec.ExecutablePath))
        {
            return Failure(LocalRuntimeReasonCodes.ExecutableMissing, started);
        }

        if (!LocalModelManifest.IsValidSha256(spec.ExpectedSha256))
        {
            return Failure(LocalRuntimeReasonCodes.RuntimeUnverified, started);
        }

        if (!Directory.Exists(spec.WorkingDirectory))
        {
            return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
        }

        if (spec.Timeout <= TimeSpan.Zero
            || spec.MaxOutputBytes < MinimumOutputBudget
            || spec.MaxOutputBytes > MaximumOutputBudget)
        {
            return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
        }

        string actualHash;
        try
        {
            actualHash = await ModelInstallStore
                .ComputeSha256Async(spec.ExecutablePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(
                false,
                null,
                cancelled: true,
                timedOut: false,
                outputTruncated: false,
                BoundedOutput.Empty,
                BoundedOutput.Empty,
                LocalRuntimeReasonCodes.ProcessCancelled,
                started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(LocalRuntimeReasonCodes.ExecutableMissing, started);
        }

        if (!string.Equals(actualHash, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(LocalRuntimeReasonCodes.ExecutableHashMismatch, started);
        }

        WindowsJobObject? job = null;
        WindowsSuspendedProcessLauncher.SuspendedLaunch? launch = null;
        Process? process = null;
        OwnedProcessIdentity? identity = null;
        StreamReader? stdoutReader = null;
        StreamReader? stderrReader = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!WindowsJobObject.TryCreate(out job, out _))
                {
                    return Failure(LocalRuntimeReasonCodes.JobCreateFailed, started);
                }

                if (!WindowsSuspendedProcessLauncher.TryStart(spec, out launch, out _))
                {
                    return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
                }

                process = launch!.Process;
                if (!job!.Assign(launch.ProcessHandle, out _))
                {
                    launch.Terminate(1);
                    await WaitForExitBestEffortAsync(process).ConfigureAwait(false);
                    return Failure(LocalRuntimeReasonCodes.JobAssignFailed, started);
                }

                identity = _registry?.Register(process, spec.ExecutablePath);
                if (!launch.Resume(out _))
                {
                    KillBestEffort(process, job);
                    await WaitForExitBestEffortAsync(process).ConfigureAwait(false);
                    return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
                }

                (stdoutReader, stderrReader) = launch.DetachStreams();
                launch.ReleaseProcessHandles();
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = spec.ExecutablePath,
                    WorkingDirectory = spec.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var argument in spec.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
                }

                identity = _registry?.Register(process, spec.ExecutablePath);
                stdoutReader = process.StandardOutput;
                stderrReader = process.StandardError;
            }

            var stdoutTask = ReadBoundedAsync(stdoutReader!, spec.MaxOutputBytes);
            var stderrTask = ReadBoundedAsync(stderrReader!, spec.MaxOutputBytes);

            var cancelled = false;
            var timedOut = false;
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                waitCts.CancelAfter(spec.Timeout);
                try
                {
                    await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = cancellationToken.IsCancellationRequested;
                    timedOut = !cancelled;
                    KillBestEffort(process, job);
                    await WaitForExitBestEffortAsync(process).ConfigureAwait(false);
                }
            }

            if (!cancelled && !timedOut)
            {
                // A root process may exit while descendants still hold the pipes.
                // Reap the job tree before draining the bounded output.
                job?.Terminate(0);
            }

            var stdout = await AwaitOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await AwaitOutputAsync(stderrTask).ConfigureAwait(false);
            if (cancelled)
            {
                return Result(
                    false,
                    null,
                    cancelled: true,
                    timedOut: false,
                    outputTruncated: stdout.Truncated || stderr.Truncated,
                    stdout,
                    stderr,
                    LocalRuntimeReasonCodes.ProcessCancelled,
                    started);
            }

            if (timedOut)
            {
                return Result(
                    false,
                    null,
                    cancelled: false,
                    timedOut: true,
                    outputTruncated: stdout.Truncated || stderr.Truncated,
                    stdout,
                    stderr,
                    LocalRuntimeReasonCodes.ProcessTimedOut,
                    started);
            }

            var exitCode = process.ExitCode;
            var outputTruncated = stdout.Truncated || stderr.Truncated;
            var succeeded = exitCode == 0 && !outputTruncated;
            return Result(
                succeeded,
                exitCode,
                cancelled: false,
                timedOut: false,
                outputTruncated,
                stdout,
                stderr,
                exitCode != 0
                    ? "process-exit-nonzero"
                    : outputTruncated
                        ? LocalRuntimeReasonCodes.OutputTruncated
                        : null,
                started);
        }
        catch (OperationCanceledException)
        {
            KillBestEffort(process, job);
            await WaitForExitBestEffortAsync(process).ConfigureAwait(false);
            return Result(
                false,
                null,
                cancelled: true,
                timedOut: false,
                outputTruncated: false,
                BoundedOutput.Empty,
                BoundedOutput.Empty,
                LocalRuntimeReasonCodes.ProcessCancelled,
                started);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            KillBestEffort(process, job);
            await WaitForExitBestEffortAsync(process).ConfigureAwait(false);
            return Failure(LocalRuntimeReasonCodes.ProcessStartFailed, started);
        }
        finally
        {
            // Keep the registry entry when exit is not proven: it is the only
            // evidence a later startup may use to clean up an escaped child.
            if (identity is not null && HasExited(process))
            {
                _registry!.Unregister(identity.ProcessId);
            }

            launch?.Dispose();
            stdoutReader?.Dispose();
            stderrReader?.Dispose();
            process?.Dispose();
            job?.Dispose();
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 16 * 1024));
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (builder.Length >= maxChars)
            {
                truncated = true;
                continue;
            }

            var take = Math.Min(read, maxChars - builder.Length);
            builder.Append(buffer, 0, take);
            if (take < read)
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            builder.Append("\n[output truncated]");
        }

        return new BoundedOutput(builder.ToString(), truncated);
    }

    private static async Task<BoundedOutput> AwaitOutputAsync(Task<BoundedOutput> task)
    {
        try
        {
            return await task.WaitAsync(KillGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new BoundedOutput(string.Empty, true);
        }
    }

    private static bool HasExited(Process? process)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void KillBestEffort(Process? process, WindowsJobObject? job)
    {
        try
        {
            if (job is not null)
            {
                job.Terminate(1);
            }
            else if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
        }
    }

    private static async Task WaitForExitBestEffortAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(KillGracePeriod).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }
    }

    private static RuntimeLaunchResult Failure(string errorCode, long started) =>
        Result(
            false,
            null,
            cancelled: false,
            timedOut: false,
            outputTruncated: false,
            BoundedOutput.Empty,
            BoundedOutput.Empty,
            errorCode,
            started);

    private static RuntimeLaunchResult Result(
        bool succeeded,
        int? exitCode,
        bool cancelled,
        bool timedOut,
        bool outputTruncated,
        BoundedOutput stdout,
        BoundedOutput stderr,
        string? errorCode,
        long started) =>
        new(
            succeeded,
            exitCode,
            cancelled,
            timedOut,
            outputTruncated,
            stdout.Text,
            stderr.Text,
            errorCode,
            Stopwatch.GetElapsedTime(started));

    private sealed record BoundedOutput(string Text, bool Truncated)
    {
        public static BoundedOutput Empty { get; } = new(string.Empty, false);
    }
}
