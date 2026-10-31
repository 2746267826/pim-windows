using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App.Services;

/// <summary>
/// WPF-facing state for the ASR processing backlog. Network operations remain
/// behind <see cref="ILocalAsrWorkerRunner"/>; when the generated adapter has not
/// been injected, every start path is disabled instead of using a fake transport.
/// </summary>
internal sealed class LocalAsrBacklogViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ILocalAsrWorkerRunner? _runner;
    private readonly ILocalAsrSchedulerSettingsStore _settingsStore;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _boundedStopTimeout;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private LocalAsrSchedulerSettings _settings = LocalAsrSchedulerSettings.Defaults;
    private LocalAccountScope? _scope;
    private bool _runnerRunnable;
    private string? _runnerStatusText;
    private long _scopeGeneration;
    private RunState? _activeRun;
    private CancellationTokenSource? _automaticLoop;
    private Task? _automaticTask;
    private bool _initialized;
    private bool _isProcessing;
    private bool _isAutomaticLoopRunning;
    private bool _automaticProcessingRequested;
    private string _statusText = "正在读取后台处理设置…";
    private LocalAsrWorkerRunStatus? _lastRunStatus;
    private string? _lastReasonCode;
    private string? _lastDetail;
    private int _consecutiveFailures;
    private DateTimeOffset? _nextAutomaticAttemptAtUtc;
    private bool _disposeRequested;
    private bool _disposed;
    private Task? _disposeTask;

    public LocalAsrBacklogViewModel(
        ILocalAsrWorkerRunner? runner,
        ILocalAsrSchedulerSettingsStore settingsStore,
        Func<DateTimeOffset>? now = null,
        TimeSpan? boundedStopTimeout = null)
    {
        _runner = runner;
        _runnerRunnable = runner is not null
            && runner is not ILocalAsrWorkerRunnerAvailability;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _boundedStopTimeout = boundedStopTimeout ?? TimeSpan.FromSeconds(2);
        if (_boundedStopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundedStopTimeout),
                "bounded stop timeout must be positive");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsWorkerAvailable
    {
        get
        {
            lock (_sync)
            {
                return _runner is not null && _runnerRunnable;
            }
        }
    }

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _initialized;
            }
        }
    }

    public bool AutomaticProcessingRequested
    {
        get
        {
            lock (_sync)
            {
                return _automaticProcessingRequested;
            }
        }
    }

    public bool AutomaticProcessingActive
    {
        get
        {
            lock (_sync)
            {
                return _isAutomaticLoopRunning;
            }
        }
    }

    public bool IsProcessing
    {
        get
        {
            lock (_sync)
            {
                return _isProcessing;
            }
        }
    }

    public bool IsScopeReady
    {
        get
        {
            lock (_sync)
            {
                return _scope is not null;
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_sync)
            {
                return _statusText;
            }
        }
    }

    public LocalAsrWorkerRunStatus? LastRunStatus
    {
        get
        {
            lock (_sync)
            {
                return _lastRunStatus;
            }
        }
    }

    public string? LastReasonCode
    {
        get
        {
            lock (_sync)
            {
                return _lastReasonCode;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    public DateTimeOffset? NextAutomaticAttemptAtUtc
    {
        get
        {
            lock (_sync)
            {
                return _nextAutomaticAttemptAtUtc;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var load = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await RefreshRunnerAvailabilityCoreAsync(scope: null, cancellationToken).ConfigureAwait(false);
        var requested = load.Settings.AutomaticProcessingEnabled;
        lock (_sync)
        {
            _settings = load.Settings with
            {
                AutomaticProcessingEnabled = requested,
            };
            _automaticProcessingRequested = requested;
            _initialized = true;
            _statusText = requested && !IsWorkerAvailable
                ? "自动处理已请求；登录服务器并选择已安装的本地模型后开始。"
                : IsWorkerAvailable
                    ? load.ResetToSafeDefaults
                    ? $"后台处理设置无效，已恢复为关闭自动处理（{load.Detail}）。"
                    : requested
                        ? "自动处理已请求；登录服务器并绑定账号后开始。"
                        : "后台处理已就绪，等待手动或自动触发。"
                : _runner is null
                    ? "ASR 服务尚未连接：需要登录服务器并取得任务凭据；当前不会发起本地或网络任务。"
                    : _runnerStatusText ?? "ASR 服务已连接，但尚未选择已安装的本地模型。";
        }

        OnPropertyChanged(nameof(IsInitialized));
        OnPropertyChanged(nameof(AutomaticProcessingRequested));
        OnPropertyChanged(nameof(StatusText));
    }

    public async Task SetScopeAsync(
        LocalAccountScope? scope,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_disposeTask is not null)
                {
                    throw new InvalidOperationException("后台处理正在退出");
                }

                if (_scope is not null
                    && scope is not null
                    && _scope.Matches(scope))
                {
                    return;
                }
            }

            if (!await StopAutomaticLoopAsync().ConfigureAwait(false))
            {
                SetStatus("自动循环未在期限内退出；未切换账号或服务器。");
                return;
            }

            if (!await CancelActiveRunAndWaitAsync().ConfigureAwait(false))
            {
                SetStatus("当前任务未在期限内退出；未切换账号或服务器。");
                return;
            }

            if (_runner is not null)
            {
                await _runner
                    .SwitchScopeAsync(scope, cancellationToken)
                    .ConfigureAwait(false);
            }

            await RefreshRunnerAvailabilityCoreAsync(scope, cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                if (_disposeRequested)
                {
                    SetStatusLocked("后台处理正在退出；未切换账号或服务器。");
                    return;
                }

                _scope = scope;
                _scopeGeneration++;
                _lastRunStatus = null;
                _lastReasonCode = null;
                _lastDetail = null;
                _consecutiveFailures = 0;
                _nextAutomaticAttemptAtUtc = null;
                _statusText = scope is null
                    ? "未登录：后台处理不会读取旧账号或旧服务器的任务。"
                    : IsWorkerAvailable
                        ? _automaticProcessingRequested
                            ? "已绑定当前账号与服务器，准备自动处理。"
                            : "已绑定当前账号与服务器，等待手动或自动触发。"
                        : _runner is null
                            ? "当前账号已绑定，但 ASR 服务尚未连接，处理保持禁用。"
                            : _runnerStatusText
                                ?? "当前账号已绑定，但尚未选择已安装的本地模型。";
            }

            OnPropertyChanged(nameof(IsScopeReady));
            OnPropertyChanged(nameof(LastRunStatus));
            OnPropertyChanged(nameof(LastReasonCode));
            OnPropertyChanged(nameof(ConsecutiveFailures));
            OnPropertyChanged(nameof(StatusText));
            if (scope is not null && !_disposeRequested)
            {
                StartAutomaticLoopIfRequested();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> SetAutomaticProcessingAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (enabled && !IsWorkerAvailable)
        {
            SetStatus("无法启用自动处理：ASR 服务尚未连接，请先登录服务器并取得任务凭据。");
            return false;
        }

        LocalAsrSchedulerSettings updated;
        lock (_sync)
        {
            updated = _settings with
            {
                AutomaticProcessingEnabled = enabled,
            };
        }

        await _settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _settings = updated;
            _automaticProcessingRequested = enabled;
            _consecutiveFailures = 0;
            _statusText = enabled ? "已启用自动处理。" : "已关闭自动处理。";
        }

        OnPropertyChanged(nameof(AutomaticProcessingRequested));
        OnPropertyChanged(nameof(ConsecutiveFailures));
        OnPropertyChanged(nameof(StatusText));
        if (enabled)
        {
            StartAutomaticLoopIfRequested();
        }
        else
        {
            return await StopAutomaticLoopAsync().ConfigureAwait(false);
        }

        return true;
    }

    public async Task RefreshRunnerAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalAccountScope? scope;
            lock (_sync)
            {
                ThrowIfDisposed();
                scope = _scope;
            }

            await RefreshRunnerAvailabilityCoreAsync(scope, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _statusText = scope is null
                    ? "未登录：后台处理不会读取旧账号或旧服务器的任务。"
                    : IsWorkerAvailable
                        ? _automaticProcessingRequested
                            ? "已绑定当前账号与服务器，准备自动处理。"
                            : "已绑定当前账号与服务器，等待手动或自动触发。"
                        : _runnerStatusText
                            ?? "当前账号已绑定，但尚未选择已安装的本地模型。";
            }

            OnPropertyChanged(nameof(IsWorkerAvailable));
            OnPropertyChanged(nameof(StatusText));
            if (scope is not null && IsWorkerAvailable)
            {
                StartAutomaticLoopIfRequested();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAsrWorkerRunResult?> ProcessOnceAsync(
        LocalAsrWorkTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_runner is null)
        {
            SetStatus("处理未启动：ASR 服务尚未连接，请先登录服务器并取得任务凭据。");
            return null;
        }

        if (!IsWorkerAvailable)
        {
            var detail = _runnerStatusText ?? "尚未选择已安装且校验通过的本地 ASR 模型。";
            SetStatus("处理未启动：" + detail);
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.InvalidConfiguration,
                LocalRuntimeReasonCodes.InvalidConfiguration,
                detail);
        }

        LocalAccountScope scope;
        lock (_sync)
        {
            if (_scope is null)
            {
                SetStatusLocked("处理未启动：需要登录当前服务器并绑定账号。");
                return null;
            }

            scope = _scope;
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            SetStatus("已有 ASR 任务在处理；未叠加第二个本地运行。");
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_disposeRequested
                || (_scope is not null && !_scope.Matches(scope)))
            {
                // A scope switch or disposal may complete after runGate was acquired
                // but before this run registered itself. Never start in that window.
                _runGate.Release();
                SetStatusLocked("账号或服务器已改变；本次任务未启动。");
                return null;
            }

            _activeRun = new RunState(
                scope,
                _scopeGeneration,
                linked,
                completion);
            _isProcessing = true;
        }

        OnPropertyChanged(nameof(IsProcessing));
        SetStatus(trigger == LocalAsrWorkTrigger.Automatic
            ? "自动处理正在检查队列…"
            : "手动处理正在检查队列…");

        LocalAsrWorkerRunResult result = null!;
        try
        {
            result = await _runner
                .ProcessOnceAsync(scope, trigger, idempotencyKey: null, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            result = new LocalAsrWorkerRunResult(
                LocalAsrWorkerRunStatus.Cancelled,
                null,
                LocalRuntimeReasonCodes.WorkerCancelled,
                null,
                Array.Empty<LocalAsrSegmentRunResult>(),
                TerminalAcknowledged: false);
        }
        catch (Exception ex)
        {
            result = new LocalAsrWorkerRunResult(
                LocalAsrWorkerRunStatus.TransportFailed,
                null,
                LocalRuntimeReasonCodes.TransportFailed,
                ex.GetType().Name,
                Array.Empty<LocalAsrSegmentRunResult>(),
                TerminalAcknowledged: false);
        }
        finally
        {
            lock (_sync)
            {
                // The result is recorded while the old scope still owns this run.
                // The completion barrier is opened only after that commit, so a
                // scope switch cannot observe "done" and then be overwritten.
                if (ReferenceEquals(_activeRun?.Cancellation, linked)
                    && _scope is not null
                    && _scope.Matches(_activeRun.Scope)
                    && _scopeGeneration == _activeRun.Generation)
                {
                    RecordRunResultLocked(result);
                }
                if (ReferenceEquals(_activeRun?.Cancellation, linked))
                {
                    _activeRun = null;
                }

                _isProcessing = false;
            }

            _runGate.Release();

            OnPropertyChanged(nameof(IsProcessing));
            OnPropertyChanged(nameof(LastRunStatus));
            OnPropertyChanged(nameof(LastReasonCode));
            OnPropertyChanged(nameof(ConsecutiveFailures));
            OnPropertyChanged(nameof(StatusText));
            completion.TrySetResult(true);
        }

        return result;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
            }

            var loopStopped = await StopAutomaticLoopAsync().ConfigureAwait(false);
            var runStopped = await CancelActiveRunAndWaitAsync().ConfigureAwait(false);
            if (!loopStopped || !runStopped)
            {
                SetStatus("停止请求尚未完成：保留本地运行与任务凭据，阻止新循环或账号切换。");
                return;
            }

            SetStatus("本地 ASR 处理已停止，受控进程退出屏障已完成。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
            }

            disposeTask = _disposeTask;
        }

        if (owner is null)
        {
            try
            {
                await disposeTask.WaitAsync(_boundedStopTimeout * 3).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The bounded owner is still cleaning an owned run. A later call
                // can observe the same task without double-disposing resources.
            }

            return;
        }

        try
        {
            // A scope switch or stop holding the gate must not hang dispose
            // forever; the barrier retry path below stays safe and re-runnable.
            // SemaphoreSlim.WaitAsync returns false on timeout; the bounded
            // barrier keeps dispose retryable instead of hanging forever.
            if (!await _operationGate
                .WaitAsync(_boundedStopTimeout * 3)
                .ConfigureAwait(false))
            {
                SetStatus("后台操作尚未让出控制权；已保留本地资源并阻止新任务。");
                return;
            }

            try
            {
                _disposeRequested = true;
                if (!await StopAutomaticLoopAsync().ConfigureAwait(false)
                    || !await CancelActiveRunAndWaitAsync().ConfigureAwait(false))
                {
                    // A later Dispose retries the barrier. Resources stay owned.
                    SetStatus("后台处理仍在退出；已保留本地资源并阻止新任务。");
                    return;
                }

                _disposed = true;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            owner.TrySetResult(_disposed);
            if (!_disposed)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_disposeTask, disposeTask))
                    {
                        _disposeTask = null;
                    }
                }
            }
        }
    }


    private sealed record RunState(
        LocalAccountScope Scope,
        long Generation,
        CancellationTokenSource Cancellation,
        TaskCompletionSource<bool> Completion);

    private void StartAutomaticLoopIfRequested()
    {
        lock (_sync)
        {
            if (_disposed
                || _disposeRequested
                || !_automaticProcessingRequested
                || !IsWorkerAvailable
                || _scope is null
                || _automaticLoop is not null)
            {
                return;
            }

            _automaticLoop = new CancellationTokenSource();
            _automaticTask = RunAutomaticLoopAsync(
                _automaticLoop,
                _automaticLoop.Token);
        }

        OnPropertyChanged(nameof(AutomaticProcessingActive));
    }

    private async Task RunAutomaticLoopAsync(
        CancellationTokenSource automaticLoop,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _isAutomaticLoopRunning = true;
        }

        OnPropertyChanged(nameof(AutomaticProcessingActive));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await ProcessOnceAsync(
                        LocalAsrWorkTrigger.Automatic,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (result is null)
                {
                    return;
                }

                if (IsFatalForAutomaticProcessing(result.Status)
                    || (_consecutiveFailures >= _settings.MaxConsecutiveFailures
                        && IsFailure(result.Status)))
                {
                    await DisableAutomaticProcessingAfterFailureAsync(result)
                        .ConfigureAwait(false);
                    return;
                }

                var delay = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
                lock (_sync)
                {
                    _nextAutomaticAttemptAtUtc = _now() + delay;
                }

                OnPropertyChanged(nameof(NextAutomaticAttemptAtUtc));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var disposeLoop = false;
            lock (_sync)
            {
                _isAutomaticLoopRunning = false;
                _nextAutomaticAttemptAtUtc = null;
                if (ReferenceEquals(_automaticLoop, automaticLoop))
                {
                    _automaticLoop = null;
                    _automaticTask = null;
                    disposeLoop = true;
                }
            }

            OnPropertyChanged(nameof(AutomaticProcessingActive));
            OnPropertyChanged(nameof(NextAutomaticAttemptAtUtc));
            if (disposeLoop)
            {
                automaticLoop.Dispose();
            }
        }
    }

    private async Task DisableAutomaticProcessingAfterFailureAsync(
        LocalAsrWorkerRunResult result)
    {
        LocalAsrSchedulerSettings updated;
        lock (_sync)
        {
            updated = _settings with
            {
                AutomaticProcessingEnabled = false,
            };
            _automaticProcessingRequested = false;
            _statusText =
                $"自动处理已熔断（{result.Status}/{result.ReasonCode ?? "unknown"}）；" +
                "手动处理仍会重新执行资源与任务凭据校验。";
        }

        try
        {
            await _settingsStore
                .SaveAsync(updated, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lock (_sync)
            {
                _statusText += " 设置落盘失败，但本次会话仍保持关闭。";
            }
        }

        lock (_sync)
        {
            _settings = updated;
        }

        OnPropertyChanged(nameof(AutomaticProcessingRequested));
        OnPropertyChanged(nameof(StatusText));
    }

    private async Task RefreshRunnerAvailabilityCoreAsync(
        LocalAccountScope? scope,
        CancellationToken cancellationToken)
    {
        if (_runner is not ILocalAsrWorkerRunnerAvailability availability)
        {
            lock (_sync)
            {
                _runnerRunnable = _runner is not null;
                _runnerStatusText = null;
            }

            return;
        }

        try
        {
            await availability
                .RefreshAvailabilityAsync(scope, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _runnerRunnable = availability.IsRunnable;
                _runnerStatusText = availability.StatusText;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            lock (_sync)
            {
                _runnerRunnable = false;
                _runnerStatusText = "ASR 服务或本地模型校验暂不可用：" + ex.GetType().Name;
            }
        }
    }

    private async Task<bool> StopAutomaticLoopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _automaticLoop;
            task = _automaticTask;
        }

        if (cancellation is null)
        {
            return true;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        var completed = true;
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(_boundedStopTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                completed = false;
            }
            catch (Exception)
            {
                // A terminal loop fault is still a completed lifetime. Its finally
                // block owns the cancellation source and completion cleanup.
            }
        }

        var disposeCancellation = false;
        if (completed)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_automaticLoop, cancellation))
                {
                    _automaticLoop = null;
                    _automaticTask = null;
                    disposeCancellation = true;
                }
            }
        }

        if (disposeCancellation)
        {
            cancellation.Dispose();
        }
        else if (!completed)
        {
            lock (_sync)
            {
                _statusText =
                    "自动循环尚未退出；保留取消源并阻止新循环与账号切换。";
            }

            OnPropertyChanged(nameof(StatusText));
        }

        OnPropertyChanged(nameof(AutomaticProcessingActive));
        return completed;
    }

    private async Task<bool> CancelActiveRunAndWaitAsync()
    {
        RunState? active;
        Task? completion;
        lock (_sync)
        {
            active = _activeRun;
            completion = active?.Completion.Task;
        }

        if (completion is null)
        {
            return true;
        }

        if (active is not null)
        {
            try
            {
                await active.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The only disposal owner is the run's own using scope.
            }
        }

        try
        {
            await completion.WaitAsync(_boundedStopTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void RecordRunResultLocked(LocalAsrWorkerRunResult result)
    {
        _lastRunStatus = result.Status;
        _lastReasonCode = result.ReasonCode;
        _lastDetail = result.Detail;
        if (result.Status == LocalAsrWorkerRunStatus.Completed)
        {
            _consecutiveFailures = 0;
        }
        else if (IsFailure(result.Status))
        {
            _consecutiveFailures++;
        }

        _statusText = FormatResult(result);
    }

    private bool IsScopeCurrent(LocalAccountScope scope)
    {
        lock (_sync)
        {
            return ReferenceEquals(_scope, scope);
        }
    }

    private static string FormatResult(LocalAsrWorkerRunResult result)
    {
        var reason = string.IsNullOrWhiteSpace(result.ReasonCode)
            ? string.Empty
            : $" / {result.ReasonCode}";
        var attempt = string.IsNullOrWhiteSpace(result.AttemptId)
            ? string.Empty
            : $" · attempt {result.AttemptId}";
        return result.Status switch
        {
            LocalAsrWorkerRunStatus.NoWork => "当前没有可领取的 ASR 任务。",
            LocalAsrWorkerRunStatus.Deferred => "服务端要求稍后重试；未启动本地运行。",
            LocalAsrWorkerRunStatus.BackoffDeferred => "自动退避仍在生效；未绕过资源检查。",
            LocalAsrWorkerRunStatus.ResourceBlocked => "资源要求未满足；未启动本地运行。",
            LocalAsrWorkerRunStatus.Completed => $"ASR 任务完成，逐片与终态回执已确认{attempt}。",
            LocalAsrWorkerRunStatus.Stopped => "ASR 任务已停止，受控进程退出后已尽力发送终态失败回执。",
            LocalAsrWorkerRunStatus.Cancelled => "ASR 任务已取消。",
            LocalAsrWorkerRunStatus.ScopeChanged => "账号或服务器已切换，旧运行已停止。",
            LocalAsrWorkerRunStatus.ExecutionUnknown => "执行结果未知；重启或换账号后不会复用旧任务凭据。",
            _ => $"ASR 处理状态：{result.Status}{reason}{attempt}。",
        };
    }

    private static bool IsFailure(LocalAsrWorkerRunStatus status) =>
        status is LocalAsrWorkerRunStatus.Failed
            or LocalAsrWorkerRunStatus.LeaseExpired
            or LocalAsrWorkerRunStatus.ExecutionUnknown
            or LocalAsrWorkerRunStatus.AckRejected
            or LocalAsrWorkerRunStatus.InvalidClaim
            or LocalAsrWorkerRunStatus.TransportFailed
            or LocalAsrWorkerRunStatus.InvalidConfiguration;

    private static bool IsFatalForAutomaticProcessing(LocalAsrWorkerRunStatus status) =>
        status is LocalAsrWorkerRunStatus.ExecutionUnknown
            or LocalAsrWorkerRunStatus.AckRejected
            or LocalAsrWorkerRunStatus.InvalidClaim
            or LocalAsrWorkerRunStatus.InvalidConfiguration
            or LocalAsrWorkerRunStatus.ScopeChanged;

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            SetStatusLocked(status);
        }
    }

    private void SetStatusLocked(string status)
    {
        _statusText = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposeRequested, this);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
