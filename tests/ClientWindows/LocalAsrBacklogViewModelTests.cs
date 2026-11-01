using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.App.Services;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class LocalAsrBacklogViewModelTests : IDisposable
{
    private static readonly LocalAccountScope Scope =
        new("https://api.example.test", "account-a");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-asr-backlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task MissingGeneratedAdapter_KeepsAllProcessingDisabled()
    {
        var settings = new LocalAsrSchedulerSettingsStore(SettingsPath());
        await using var viewModel = new LocalAsrBacklogViewModel(null, settings);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsWorkerAvailable);
        Assert.False(viewModel.AutomaticProcessingRequested);
        Assert.False(await viewModel.SetAutomaticProcessingAsync(true));
        Assert.Null(await viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual));
        Assert.Contains("ASR 服务尚未连接", viewModel.StatusText);
        Assert.Contains("任务凭据", viewModel.StatusText);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsBoundedValuesAndRejectsCorruptionSafely()
    {
        var store = new LocalAsrSchedulerSettingsStore(SettingsPath());
        var expected = new LocalAsrSchedulerSettings(
            AutomaticProcessingEnabled: true,
            PollIntervalSeconds: 45,
            MaxConsecutiveFailures: 5,
            UpdatedAtUtc: DateTimeOffset.MinValue);

        await store.SaveAsync(expected);
        var loaded = await store.LoadAsync();

        Assert.False(loaded.ResetToSafeDefaults);
        Assert.True(loaded.Settings.AutomaticProcessingEnabled);
        Assert.Equal(45, loaded.Settings.PollIntervalSeconds);
        Assert.Equal(5, loaded.Settings.MaxConsecutiveFailures);
        Assert.True(loaded.Settings.UpdatedAtUtc > DateTimeOffset.MinValue);

        await File.WriteAllTextAsync(SettingsPath(), "{not-json");
        var corrupted = await store.LoadAsync();

        Assert.True(corrupted.ResetToSafeDefaults);
        Assert.False(corrupted.Settings.AutomaticProcessingEnabled);
        Assert.Equal(
            LocalAsrSchedulerSettings.Defaults.PollIntervalSeconds,
            corrupted.Settings.PollIntervalSeconds);
    }

    [Fact]
    public async Task ManualProcessing_IsSingleFlightAndDoesNotStackRuns()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, _) =>
        {
            started.TrySetResult(true);
            // Cancellation must be observed through the runner callback path.
            await release.Task;
            return Result(LocalAsrWorkerRunStatus.Completed);
        });
        await using var viewModel = CreateViewModel(runner);
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);

        var first = viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);

        Assert.Null(second);
        Assert.Equal(1, runner.ProcessCallCount);
        Assert.Contains("未叠加第二个", viewModel.StatusText);

        release.TrySetResult(true);
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(completed);
        Assert.Equal(LocalAsrWorkerRunStatus.Completed, completed!.Status);
        Assert.Equal(0, viewModel.ConsecutiveFailures);
    }

    [Fact]
    public async Task DisposeTimeout_KeepsRunGateAliveUntilLateRunFinallyCompletes()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(LocalAsrWorkerRunStatus.Completed);
        });
        await using var viewModel = CreateViewModel(
            runner,
            TimeSpan.FromMilliseconds(80));
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);

        var run = viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(run.IsCompleted);
        Assert.Contains("仍在退出", viewModel.StatusText);

        release.TrySetResult(true);
        var completed = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(completed);
        Assert.Equal(LocalAsrWorkerRunStatus.Completed, completed!.Status);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDispose_WaitsFiniteAndRetriesAfterOwnedRunExits()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(LocalAsrWorkerRunStatus.Completed);
        });
        var viewModel = CreateViewModel(runner, TimeSpan.FromMilliseconds(80));
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);

        var run = viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstDispose = viewModel.DisposeAsync().AsTask();
        var secondDispose = viewModel.DisposeAsync().AsTask();

        await Task.WhenAll(firstDispose, secondDispose)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);

        release.TrySetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual));
    }

    [Fact]
    public async Task ConcurrentDispose_StalledScopeSwitch_IsFiniteAndRetryable()
    {
        var enteredSwitch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner((_, _, _) =>
            Task.FromResult(Result(LocalAsrWorkerRunStatus.Completed)))
        {
            OnSwitchAsync = async _ =>
            {
                enteredSwitch.TrySetResult(true);
                await releaseSwitch.Task;
            },
        };
        var viewModel = CreateViewModel(runner, TimeSpan.FromMilliseconds(60));
        await viewModel.InitializeAsync();

        var switching = viewModel.SetScopeAsync(Scope);
        await enteredSwitch.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposal = viewModel.DisposeAsync().AsTask();

        var completed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(disposal, completed);
        Assert.NotEqual(TaskStatus.RanToCompletion, switching.Status);

        releaseSwitch.TrySetResult(true);
        await switching.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual));
    }

    [Fact]
    public async Task StopAndDispose_DelayedRunQueueWithoutDisposedCancellation()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(LocalAsrWorkerRunStatus.Completed);
        });
        var viewModel = CreateViewModel(runner, TimeSpan.FromMilliseconds(80));
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);

        var run = viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = viewModel.StopAsync();
        var dispose = viewModel.DisposeAsync().AsTask();
        await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(3));

        release.TrySetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AutomaticLoopTimeout_BlocksScopeSwitchThenAllowsRetryAfterExit()
    {
        var store = new LocalAsrSchedulerSettingsStore(SettingsPath());
        await store.SaveAsync(new LocalAsrSchedulerSettings(
            AutomaticProcessingEnabled: true,
            PollIntervalSeconds: 5,
            MaxConsecutiveFailures: 3,
            UpdatedAtUtc: DateTimeOffset.MinValue));
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(LocalAsrWorkerRunStatus.NoWork);
        });
        await using var viewModel = new LocalAsrBacklogViewModel(
            runner,
            store,
            boundedStopTimeout: TimeSpan.FromMilliseconds(80));
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var initialSwitchCount = runner.SwitchCallCount;

        await viewModel
            .SetScopeAsync(new LocalAccountScope("https://api-2.example.test", "account-a"))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(initialSwitchCount, runner.SwitchCallCount);
        Assert.True(ReferenceEquals(Scope, runner.LastSwitchScope));
        Assert.Contains("未切换账号或服务器", viewModel.StatusText);

        release.TrySetResult(true);
        await WaitUntilAsync(
            () => !viewModel.AutomaticProcessingActive,
            TimeSpan.FromSeconds(2));
        await viewModel.SetAutomaticProcessingAsync(false);
        var nextScope = new LocalAccountScope("https://api-2.example.test", "account-a");
        await viewModel.SetScopeAsync(nextScope);

        Assert.Equal(initialSwitchCount + 1, runner.SwitchCallCount);
        Assert.True(ReferenceEquals(nextScope, runner.LastSwitchScope));
    }

    [Fact]
    public async Task LateCancelledRun_CannotOverwriteNewScopeAndNextRunUsesNewGeneration()
    {
        LocalAccountScope? currentScope = null;
        var firstStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextScope = new LocalAccountScope(
            "https://api-2.example.test",
            "account-a");
        var runner = new ScriptedRunner(async (_, _, token) =>
        {
            firstStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result(LocalAsrWorkerRunStatus.TransportFailed);
        })
        {
            OnSwitch = scope => currentScope = scope,
        };
        await using var viewModel = CreateViewModel(runner);
        await viewModel.InitializeAsync();
        await viewModel.SetScopeAsync(Scope);

        var oldRun = viewModel.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Scope, currentScope);

        var switchTask = viewModel.SetScopeAsync(nextScope);
        await WaitUntilAsync(
            () => !viewModel.IsProcessing,
            TimeSpan.FromSeconds(2));
        await switchTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(nextScope, currentScope);

        var completed = await oldRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(completed);
        Assert.Equal(LocalAsrWorkerRunStatus.Cancelled, completed!.Status);
        Assert.Equal(
            "已绑定当前账号与服务器，等待手动或自动触发。",
            viewModel.StatusText);
        // New-scope semantics clear old result/failure evidence. Scope status was
        // committed after the cancelled run, so it must remain the new-scope text.
        Assert.Null(viewModel.LastRunStatus);
        Assert.Equal(0, viewModel.ConsecutiveFailures);
    }

    [Fact]
    public async Task AutomaticProcessing_StartsOnlyAfterScopeAndStopsOnScopeRemoval()
    {
        var store = new LocalAsrSchedulerSettingsStore(SettingsPath());
        await store.SaveAsync(new LocalAsrSchedulerSettings(
            AutomaticProcessingEnabled: true,
            PollIntervalSeconds: 5,
            MaxConsecutiveFailures: 3,
            UpdatedAtUtc: DateTimeOffset.MinValue));
        var firstCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner((_, _, _) =>
        {
            firstCall.TrySetResult(true);
            return Task.FromResult(Result(LocalAsrWorkerRunStatus.NoWork));
        });
        await using var viewModel = new LocalAsrBacklogViewModel(runner, store);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.AutomaticProcessingRequested);
        Assert.False(viewModel.AutomaticProcessingActive);

        await viewModel.SetScopeAsync(Scope);
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.AutomaticProcessingActive);

        await viewModel.SetScopeAsync(null);

        Assert.False(viewModel.AutomaticProcessingActive);
        Assert.Null(runner.LastSwitchScope);
        Assert.True(runner.SwitchCallCount >= 2);
    }

    [Fact]
    public async Task SavedAutomaticPreference_SurvivesUnavailableStartupAndActivatesLater()
    {
        var store = new LocalAsrSchedulerSettingsStore(SettingsPath());
        await store.SaveAsync(new LocalAsrSchedulerSettings(
            AutomaticProcessingEnabled: true,
            PollIntervalSeconds: 5,
            MaxConsecutiveFailures: 3,
            UpdatedAtUtc: DateTimeOffset.MinValue));
        var firstCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AvailabilityRunner((_, _, _) =>
        {
            firstCall.TrySetResult(true);
            return Task.FromResult(Result(LocalAsrWorkerRunStatus.NoWork));
        })
        {
            IsRunnable = false,
        };
        await using var viewModel = new LocalAsrBacklogViewModel(runner, store);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.AutomaticProcessingRequested);
        Assert.False(viewModel.AutomaticProcessingActive);
        Assert.Contains("自动处理已请求", viewModel.StatusText);

        runner.IsRunnable = true;
        await viewModel.SetScopeAsync(Scope);
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.AutomaticProcessingRequested);
        Assert.True(viewModel.AutomaticProcessingActive);
        Assert.True((await store.LoadAsync()).Settings.AutomaticProcessingEnabled);

        await viewModel.SetAutomaticProcessingAsync(false);
        await viewModel.SetScopeAsync(null);
    }

    [Fact]
    public async Task FatalAutomaticRun_DisablesAndPersistsAutomaticProcessing()
    {
        var store = new LocalAsrSchedulerSettingsStore(SettingsPath());
        await store.SaveAsync(new LocalAsrSchedulerSettings(
            AutomaticProcessingEnabled: true,
            PollIntervalSeconds: 5,
            MaxConsecutiveFailures: 3,
            UpdatedAtUtc: DateTimeOffset.MinValue));
        var secondCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var runner = new ScriptedRunner((_, _, _) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                return Task.FromResult(Result(LocalAsrWorkerRunStatus.ExecutionUnknown));
            }

            secondCall.TrySetResult(true);
            return Task.FromResult(Result(LocalAsrWorkerRunStatus.NoWork));
        });
        await using var viewModel = new LocalAsrBacklogViewModel(runner, store);
        await viewModel.InitializeAsync();

        await viewModel.SetScopeAsync(Scope);
        await WaitUntilAsync(
            () => !viewModel.AutomaticProcessingRequested,
            TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => PersistedSettingsContain(
                "\"automaticProcessingEnabled\": false"),
            TimeSpan.FromSeconds(2));

        var persistedAfterDisable = await store.LoadAsync();
        Assert.False(viewModel.AutomaticProcessingRequested);
        Assert.Contains("熔断", viewModel.StatusText);
        Assert.False(persistedAfterDisable.Settings.AutomaticProcessingEnabled);

        Assert.True(await viewModel.SetAutomaticProcessingAsync(true));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.AutomaticProcessingActive);
        await viewModel.SetScopeAsync(null);
    }

    private LocalAsrBacklogViewModel CreateViewModel(
        ScriptedRunner runner,
        TimeSpan? boundedStopTimeout = null) =>
        new(
            runner,
            new LocalAsrSchedulerSettingsStore(SettingsPath()),
            boundedStopTimeout: boundedStopTimeout);

    private string SettingsPath() => Path.Combine(_root, "scheduler.json");

    private bool PersistedSettingsContain(string expected)
    {
        try
        {
            return File.Exists(SettingsPath())
                && File.ReadAllText(SettingsPath()).Contains(
                    expected,
                    StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static LocalAsrWorkerRunResult Result(LocalAsrWorkerRunStatus status) =>
        new(
            status,
            null,
            status == LocalAsrWorkerRunStatus.ExecutionUnknown
                ? LocalRuntimeReasonCodes.RenewFailed
                : null,
            null,
            Array.Empty<LocalAsrSegmentRunResult>(),
            TerminalAcknowledged: status == LocalAsrWorkerRunStatus.Completed);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("condition was not reached");
            }

            await Task.Delay(10);
        }
    }

    private class ScriptedRunner : ILocalAsrWorkerRunner
    {
        private readonly Func<
            LocalAccountScope,
            LocalAsrWorkTrigger,
            CancellationToken,
            Task<LocalAsrWorkerRunResult>> _handler;

        public ScriptedRunner(
            Func<
                LocalAccountScope,
                LocalAsrWorkTrigger,
                CancellationToken,
                Task<LocalAsrWorkerRunResult>> handler)
        {
            _handler = handler;
        }

        public int ProcessCallCount { get; private set; }

        public int SwitchCallCount { get; private set; }

        public LocalAccountScope? LastSwitchScope { get; private set; }

        public Action<LocalAccountScope?>? OnSwitch { get; init; }

        public Func<LocalAccountScope?, Task>? OnSwitchAsync { get; init; }

        public Task<LocalAsrWorkerRunResult> ProcessOnceAsync(
            LocalAccountScope scope,
            LocalAsrWorkTrigger trigger,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            ProcessCallCount++;
            return _handler(scope, trigger, cancellationToken);
        }

        public async Task<LocalAsrScopeSwitchResult> SwitchScopeAsync(
            LocalAccountScope? newScope,
            CancellationToken cancellationToken = default)
        {
            SwitchCallCount++;
            LastSwitchScope = newScope;
            var result = new LocalAsrScopeSwitchResult(
                PreviousScope: null,
                CurrentScope: newScope,
                new AccountSwitchResult(null, newScope?.OwnerIdentity, false, false),
                Withdraw: null);
            OnSwitch?.Invoke(newScope);
            if (OnSwitchAsync is not null)
            {
                await OnSwitchAsync(newScope);
            }

            return result;
        }
    }

    private sealed class AvailabilityRunner : ScriptedRunner, ILocalAsrWorkerRunnerAvailability
    {
        public AvailabilityRunner(
            Func<
                LocalAccountScope,
                LocalAsrWorkTrigger,
                CancellationToken,
                Task<LocalAsrWorkerRunResult>> handler)
            : base(handler)
        {
        }

        public bool IsRunnable { get; set; }

        public string StatusText => IsRunnable
            ? "available"
            : "local model not installed";

        public Task RefreshAvailabilityAsync(
            LocalAccountScope? scope,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
