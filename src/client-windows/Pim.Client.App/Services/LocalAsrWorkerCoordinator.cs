using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App.Services;

internal sealed record LocalAsrWorkerSelection(
    string ServerBaseAddress,
    string OwnerIdentity,
    string RuntimeId,
    string ModelId,
    DateTimeOffset SelectedAtUtc);

internal sealed record LocalAsrWorkerSelectionResult(
    bool Selected,
    string? ReasonCode,
    string? Detail);

internal interface ILocalAsrWorkerSelection
{
    Task<LocalAsrWorkerSelectionResult> SelectAsync(
        LocalAccountScope scope,
        string runtimeId,
        string modelId,
        CancellationToken cancellationToken = default);

    Task ClearSelectionAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default);
}

internal interface ILocalAsrWorkerRunnerAvailability
{
    bool IsRunnable { get; }

    string StatusText { get; }

    Task RefreshAvailabilityAsync(
        LocalAccountScope? scope,
        CancellationToken cancellationToken = default);
}

internal interface ILocalAsrWorkerSelectionStore
{
    Task<LocalAsrWorkerSelection?> LoadAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        LocalAsrWorkerSelection selection,
        CancellationToken cancellationToken = default);

    Task ClearAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default);
}

internal sealed class LocalAsrWorkerSelectionStore : ILocalAsrWorkerSelectionStore
{
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;

    public LocalAsrWorkerSelectionStore(string root, Func<DateTimeOffset>? now = null)
    {
        _root = Path.GetFullPath(root);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LocalAsrWorkerSelection?> LoadAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var path = SelectionPath(scope);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var selection = JsonSerializer.Deserialize<LocalAsrWorkerSelection>(json);
            return IsValid(scope, selection) ? selection : null;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        LocalAsrWorkerSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!LocalModelManifest.IsValidModelId(selection.RuntimeId)
            || !LocalModelManifest.IsValidModelId(selection.ModelId))
        {
            throw new ArgumentException("runtime and model ids are required", nameof(selection));
        }

        var scope = new LocalAccountScope(
            selection.ServerBaseAddress,
            selection.OwnerIdentity);
        var path = SelectionPath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var normalized = selection with { SelectedAtUtc = _now() };
        try
        {
            await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(normalized),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public Task ClearAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete(SelectionPath(scope));
        return Task.CompletedTask;
    }

    private string SelectionPath(LocalAccountScope scope)
    {
        var scopeKey = PathSafety.HashAccountKey(
            scope.ServerBaseAddress + "\n" + scope.OwnerIdentity);
        return Path.Combine(_root, "accounts", scopeKey, "asr-worker", "selection.json");
    }

    private static bool IsValid(
        LocalAccountScope expectedScope,
        LocalAsrWorkerSelection? selection) =>
        selection is not null
        && string.Equals(
            selection.ServerBaseAddress,
            expectedScope.ServerBaseAddress,
            StringComparison.Ordinal)
        && string.Equals(
            selection.OwnerIdentity,
            expectedScope.OwnerIdentity,
            StringComparison.Ordinal)
        && LocalModelManifest.IsValidModelId(selection.RuntimeId)
        && LocalModelManifest.IsValidModelId(selection.ModelId);

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
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Resolves exactly one selected, freshly verified local catalog pair before
/// registration, heartbeat, claim, or execution. Missing/stale selection is a
/// safe InvalidConfiguration result, never a synthetic run or automatic download.
/// </summary>
internal sealed class LocalAsrWorkerCoordinator :
    ILocalAsrWorkerRunner,
    ILocalAsrWorkerSelection,
    ILocalAsrWorkerRunnerAvailability
{
    private readonly LocalAsrApiTransport _transport;
    private readonly InstalledLocalAsrArtifactVerifier _verifier;
    private readonly LocalRuntimeAccountGuard _guard;
    private readonly ILocalSpeechRuntimeAdapter _runtimeAdapter;
    private readonly IResourceSnapshotProvider _resourceProvider;
    private readonly LocalRuntimeCatalogProvider _catalogProvider;
    private readonly ILocalAsrWorkerSelectionStore _selectionStore;
    private readonly Func<LocalAccountScope?> _currentScope;
    private readonly string _root;
    private readonly string _installationId;
    private readonly string _nodeDisplayName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private PreparedEngine? _prepared;
    private bool _isRunnable;
    private string _statusText = "尚未选择已安装的本地 ASR 模型。";

    public LocalAsrWorkerCoordinator(
        LocalAsrApiTransport transport,
        InstalledLocalAsrArtifactVerifier verifier,
        LocalRuntimeAccountGuard guard,
        ILocalSpeechRuntimeAdapter runtimeAdapter,
        IResourceSnapshotProvider resourceProvider,
        LocalRuntimeCatalogProvider catalogProvider,
        ILocalAsrWorkerSelectionStore selectionStore,
        Func<LocalAccountScope?> currentScope,
        string root,
        string? nodeDisplayName = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _runtimeAdapter = runtimeAdapter ?? throw new ArgumentNullException(nameof(runtimeAdapter));
        _resourceProvider = resourceProvider ?? throw new ArgumentNullException(nameof(resourceProvider));
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        _currentScope = currentScope ?? throw new ArgumentNullException(nameof(currentScope));
        _root = Path.GetFullPath(root);
        _installationId = LoadOrCreateInstallationId(_root);
        _nodeDisplayName = NormalizeNodeDisplayName(nodeDisplayName ?? Environment.MachineName);
    }

    public bool IsRunnable
    {
        get
        {
            lock (_sync)
            {
                return _isRunnable;
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

    public async Task RefreshAvailabilityAsync(
        LocalAccountScope? scope,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveSelectionAsync(
                    scope,
                    verifyDigests: true,
                    cancellationToken)
                .ConfigureAwait(false);
            SetAvailability(
                resolved.Error is null,
                resolved.Error
                ?? $"已选择 {resolved.Model?.DisplayName}，本地安装校验通过。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAsrWorkerSelectionResult> SelectAsync(
        LocalAccountScope scope,
        string runtimeId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!LocalModelManifest.IsValidModelId(runtimeId)
            || !LocalModelManifest.IsValidModelId(modelId))
        {
            return new(false, "invalid-selection", "运行时或模型标识无效。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentScope(scope))
            {
                return new(false, LocalRuntimeReasonCodes.ScopeChanged, "账号或服务器已改变。");
            }

            var catalog = await _catalogProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
            var runtime = catalog.Parse.Catalog.Runtimes.FirstOrDefault(item =>
                string.Equals(item.RuntimeId, runtimeId, StringComparison.Ordinal));
            var model = catalog.Parse.Catalog.Models.FirstOrDefault(item =>
                string.Equals(item.ModelId, modelId, StringComparison.Ordinal));
            var selectionError = ValidateSelection(runtime, model);
            if (selectionError is not null)
            {
                SetAvailability(false, selectionError);
                return new(false, "invalid-selection", selectionError);
            }

            if (!await _verifier.VerifyAsync(runtime!, model!, cancellationToken).ConfigureAwait(false))
            {
                const string unavailable = "所选模型或运行时的本账号安装、大小或摘要校验失败。";
                SetAvailability(false, unavailable);
                return new(false, "installed-proof-failed", unavailable);
            }

            if (!IsCurrentScope(scope))
            {
                return new(false, LocalRuntimeReasonCodes.ScopeChanged, "账号或服务器已改变。");
            }

            await _selectionStore.SaveAsync(
                    new LocalAsrWorkerSelection(
                        scope.ServerBaseAddress,
                        scope.OwnerIdentity,
                        runtime!.RuntimeId,
                        model!.ModelId,
                        DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

            var prepared = CreatePreparedEngine(scope, runtime, model);
            var registration = await RegisterAndHeartbeatAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            if (registration is not null)
            {
                _prepared = null;
                SetAvailability(false, registration);
                return new(false, "registration-failed", registration);
            }

            _prepared = prepared;
            SetAvailability(true, $"已选择 {model.DisplayName}，节点注册与心跳已完成。");
            return new(true, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or HttpRequestException
                or JsonException)
        {
            var detail = $"本地 ASR 选型失败：{ex.GetType().Name}";
            SetAvailability(false, detail);
            return new(false, LocalRuntimeReasonCodes.InvalidConfiguration, detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearSelectionAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _selectionStore.ClearAsync(scope, cancellationToken).ConfigureAwait(false);
            _prepared = null;
            SetAvailability(false, "尚未选择已安装的本地 ASR 模型。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAsrWorkerRunResult> ProcessOnceAsync(
        LocalAccountScope scope,
        LocalAsrWorkTrigger trigger,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveSelectionAsync(
                    scope,
                    verifyDigests: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                SetAvailability(false, resolved.Error);
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.InvalidConfiguration,
                    LocalRuntimeReasonCodes.InvalidConfiguration,
                    resolved.Error);
            }

            var prepared = _prepared;
            if (prepared is null
                || !prepared.Scope.Matches(scope)
                || !string.Equals(
                    prepared.Runtime.RuntimeId,
                    resolved.Runtime!.RuntimeId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Model.ModelId,
                    resolved.Model!.ModelId,
                    StringComparison.Ordinal))
            {
                prepared = CreatePreparedEngine(scope, resolved.Runtime!, resolved.Model!);
            }

            var registration = await RegisterAndHeartbeatAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            if (registration is not null)
            {
                SetAvailability(false, registration);
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.TransportFailed,
                    LocalRuntimeReasonCodes.TransportFailed,
                    registration);
            }

            _prepared = prepared;
            SetAvailability(true, $"已选择 {prepared.Model.DisplayName}，正在处理排队任务。");
            var recovery = await prepared.Engine
                .RecoverAfterRestartAsync(scope, cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Status == LocalAsrRestartRecoveryStatus.ExecutionUnknown)
            {
                return new LocalAsrWorkerRunResult(
                    LocalAsrWorkerRunStatus.ExecutionUnknown,
                    recovery.AttemptId,
                    LocalRuntimeReasonCodes.RestartUnknown,
                    recovery.Detail,
                    Array.Empty<LocalAsrSegmentRunResult>(),
                    TerminalAcknowledged: false);
            }

            var result = await prepared.Engine
                .ProcessOnceAsync(scope, trigger, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status is LocalAsrWorkerRunStatus.NoWork
                or LocalAsrWorkerRunStatus.Deferred
                or LocalAsrWorkerRunStatus.BackoffDeferred
                or LocalAsrWorkerRunStatus.ResourceBlocked
                or LocalAsrWorkerRunStatus.Completed)
            {
                SetAvailability(
                    true,
                    result.Status == LocalAsrWorkerRunStatus.Completed
                        ? $"已选择 {prepared.Model.DisplayName}，最近一次任务已完成。"
                        : $"已选择 {prepared.Model.DisplayName}，后台处理可用。");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.Cancelled,
                LocalRuntimeReasonCodes.WorkerCancelled);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or HttpRequestException
                or JsonException)
        {
            var detail = ex.GetType().Name;
            SetAvailability(false, $"本地 ASR 处理暂不可用：{detail}");
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.InvalidConfiguration,
                LocalRuntimeReasonCodes.InvalidConfiguration,
                detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAsrScopeSwitchResult> SwitchScopeAsync(
        LocalAccountScope? newScope,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prepared = _prepared;
            LocalAsrScopeSwitchResult result;
            if (prepared is not null)
            {
                result = await prepared.Engine
                    .SwitchScopeAsync(newScope, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var previousScope = CurrentGuardScope();
                var accountSwitch = await _guard
                    .SwitchScopeAsync(newScope)
                    .ConfigureAwait(false);
                result = new LocalAsrScopeSwitchResult(
                    previousScope,
                    newScope,
                    accountSwitch,
                    Withdraw: null);
            }

            _prepared = null;
            SetAvailability(
                false,
                newScope is null
                    ? "未登录：本地 ASR 处理已停止。"
                    : "账号或服务器已切换；请重新选择本账号已安装的模型。");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResolvedSelection> ResolveSelectionAsync(
        LocalAccountScope? scope,
        bool verifyDigests,
        CancellationToken cancellationToken)
    {
        if (scope is null || !IsCurrentScope(scope))
        {
            return new(null, null, null, "需要登录当前服务器并取得任务凭据。");
        }

        var selection = await _selectionStore
            .LoadAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        if (selection is null)
        {
            return new(null, null, null, "尚未选择已安装的本地 ASR 模型。");
        }

        var catalog = await _catalogProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
        var runtime = catalog.Parse.Catalog.Runtimes.FirstOrDefault(item =>
            string.Equals(item.RuntimeId, selection.RuntimeId, StringComparison.Ordinal));
        var model = catalog.Parse.Catalog.Models.FirstOrDefault(item =>
            string.Equals(item.ModelId, selection.ModelId, StringComparison.Ordinal));
        var error = ValidateSelection(runtime, model);
        if (error is not null)
        {
            return new(runtime, model, selection, error);
        }

        if (verifyDigests
            && !await _verifier.VerifyAsync(runtime!, model!, cancellationToken).ConfigureAwait(false))
        {
            return new(
                runtime,
                model,
                selection,
                "所选模型或运行时的本账号安装、大小或摘要校验失败。");
        }

        return new(runtime, model, selection, null);
    }

    private PreparedEngine CreatePreparedEngine(
        LocalAccountScope scope,
        LocalRuntimeManifest runtime,
        LocalModelManifest model)
    {
        var nodeId = StableNodeId(scope, _installationId);
        var options = new LocalAsrWorkerOptions(
            nodeId,
            _nodeDisplayName,
            runtime,
            model,
            Path.Combine(_root, "staging"));
        var engine = new LocalAsrWorkerEngine(
            _transport,
            _runtimeAdapter,
            _guard,
            _resourceProvider,
            new RuntimeBackoffStore(Path.Combine(_root, "backoff")),
            new LocalAsrWorkerStateStore(_root),
            options);
        return new PreparedEngine(engine, runtime, model, scope, nodeId);
    }

    private async Task<string?> RegisterAndHeartbeatAsync(
        PreparedEngine prepared,
        CancellationToken cancellationToken)
    {
        ResourceSnapshot snapshot;
        try
        {
            snapshot = await _resourceProvider
                .GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            snapshot = ResourceSnapshot.Unknown(DateTimeOffset.UtcNow);
        }

        string? snapshotJson = JsonSerializer.Serialize(snapshot);
        if (snapshotJson.Length > 16_384)
        {
            snapshotJson = null;
        }

        var registration = await _transport.RegisterAsync(
                prepared.NodeId,
                _nodeDisplayName,
                prepared.Runtime,
                prepared.Model,
                enabled: true,
                resourceSnapshotJson: snapshotJson,
                sampledAt: snapshot.MeasuredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return "ASR 服务未接受当前节点的模型注册。";
        }

        var heartbeat = await _transport.HeartbeatAsync(
                prepared.NodeId,
                prepared.Runtime,
                prepared.Model,
                resourceSnapshotJson: snapshotJson,
                sampledAt: snapshot.MeasuredAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return heartbeat is null ? "ASR 服务未接受当前节点的心跳。" : null;
    }

    private void SetAvailability(bool runnable, string statusText)
    {
        lock (_sync)
        {
            _isRunnable = runnable;
            _statusText = statusText;
        }
    }

    private bool IsCurrentScope(LocalAccountScope scope)
    {
        var current = _currentScope();
        return current is not null && scope.Matches(current);
    }

    private LocalAccountScope? CurrentGuardScope()
    {
        var accountId = _guard.CurrentAccountId;
        var server = _guard.CurrentServerBaseAddress;
        return accountId is null || server is null
            ? null
            : new LocalAccountScope(server, accountId);
    }

    private static string? ValidateSelection(
        LocalRuntimeManifest? runtime,
        LocalModelManifest? model)
    {
        if (runtime is null || model is null)
        {
            return "所选模型或运行时已不在当前目录中，请刷新后重新选择。";
        }

        if (!runtime.HasVerifiedRuntime || !model.HasVerifiedArtifact)
        {
            return "所选模型或运行时缺少已登记的来源、修订、大小或摘要。";
        }

        return string.Equals(runtime.RuntimeId, model.RuntimeId, StringComparison.Ordinal)
            ? null
            : "所选模型与运行时不属于同一执行组合。";
    }

    private static string StableNodeId(
        LocalAccountScope scope,
        string installationId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            "pim-local-asr-node\n"
            + installationId
            + "\n"
            + scope.ServerBaseAddress
            + "\n"
            + scope.OwnerIdentity));
        var guidBytes = bytes[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private static string LoadOrCreateInstallationId(string root)
    {
        var directory = Path.Combine(root, "installation");
        var path = Path.Combine(directory, "asr-node-installation-id");
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (IsValidInstallationId(existing))
            {
                return existing;
            }
        }

        var created = Convert
            .ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 256,
                FileOptions.WriteThrough))
            {
                var bytes = Encoding.ASCII.GetBytes(created);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return created;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
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

    private static bool IsValidInstallationId(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static string NormalizeNodeDisplayName(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "PIM-Windows" : value.Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private sealed record PreparedEngine(
        LocalAsrWorkerEngine Engine,
        LocalRuntimeManifest Runtime,
        LocalModelManifest Model,
        LocalAccountScope Scope,
        string NodeId);

    private sealed record ResolvedSelection(
        LocalRuntimeManifest? Runtime,
        LocalModelManifest? Model,
        LocalAsrWorkerSelection? Selection,
        string? Error);
}
