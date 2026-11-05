using System.Net.Http;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed record AppKnowledgeAccountScope(string ServerUrl, string OwnerId);

/// <summary>Owner-bound client for PC category trees and application context rules.</summary>
public sealed class AppKnowledgeService : IDisposable
{
    private readonly ApiClient _api;
    private readonly Func<AppKnowledgeAccountScope?> _accountProvider;
    private long _generation;
    private bool _disposed;

    public AppKnowledgeService(ApiClient api, Func<AppKnowledgeAccountScope?> accountProvider)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _accountProvider = accountProvider ?? throw new ArgumentNullException(nameof(accountProvider));
        _api.ExternalScopeChanged += OnExternalScopeChanged;
    }

    public long Generation => Interlocked.Read(ref _generation);

    public AppKnowledgeAccountScope? CurrentAccount
    {
        get
        {
            var account = _accountProvider();
            if (account is null)
            {
                return null;
            }

            return NormalizeAccount(account);
        }
    }

    public async Task<IReadOnlyList<CategoryTreeNode>> GetCategoryTreeAsync(CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        var response = await AwaitCurrentAsync(
            _api.GetPcCategoryTreeAsync(ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        return RequireData(response?.Code, response?.Message, response?.Data, "读取分类树");
    }

    public async Task<CategoryTreeNode> SaveCategoryAsync(
        CategorySaveRequest request,
        AppKnowledgeAccountScope expectedAccount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedAccount);
        using var operation = BeginOperation(expectedAccount);
        RequireCurrent(operation);

        if (request.Id is { } id)
        {
            var tree = await AwaitCurrentAsync(
                _api.GetPcCategoryTreeAsync(ct),
                operation,
                ct).ConfigureAwait(false);
            RequireCurrent(operation);
            var nodes = RequireData(tree?.Code, tree?.Message, tree?.Data, "读取分类树");
            var existing = FindCategory(nodes, ParseGuid(id, nameof(request.Id)));
            if (existing is null)
            {
                throw new InvalidOperationException("分类已不存在，请刷新后重试。");
            }
            if (existing.IsBuiltin)
            {
                throw new InvalidOperationException("内置分类不可编辑。");
            }
        }

        var response = await AwaitCurrentAsync(
            _api.SavePcCategoryAsync(request, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        return RequireData(response?.Code, response?.Message, response?.Data, "保存分类");
    }

    public async Task DeleteCategoryAsync(
        Guid id,
        AppKnowledgeAccountScope expectedAccount,
        CancellationToken ct = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("分类标识无效。", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(expectedAccount);
        using var operation = BeginOperation(expectedAccount);
        var tree = await AwaitCurrentAsync(
            _api.GetPcCategoryTreeAsync(ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        var nodes = RequireData(tree?.Code, tree?.Message, tree?.Data, "读取分类树");
        var category = FindCategory(nodes, id)
            ?? throw new InvalidOperationException("分类已不存在，请刷新后重试。");
        if (category.IsBuiltin)
        {
            throw new InvalidOperationException("内置分类不可删除。");
        }

        var response = await AwaitCurrentAsync(
            _api.DeletePcCategoryAsync(id, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        RequireDeleteAcknowledgement(response?.Code, response?.Message, response?.Data, "已删除", "删除分类");
    }

    public async Task<IReadOnlyList<AppKnowledgeAppDto>> SearchAppsAsync(string? search, CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        var response = await AwaitCurrentAsync(
            _api.SearchAppKnowledgeAppsAsync(search, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        return RequireData(response?.Code, response?.Message, response?.Data, "搜索应用知识");
    }

    public async Task<IReadOnlyList<AppKnowledgeContextDto>> GetContextsAsync(Guid appId, CancellationToken ct = default)
    {
        if (appId == Guid.Empty)
        {
            throw new ArgumentException("应用标识无效。", nameof(appId));
        }

        using var operation = BeginOperation();
        var response = await AwaitCurrentAsync(
            _api.GetAppKnowledgeContextsAsync(appId, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        return RequireData(response?.Code, response?.Message, response?.Data, "读取应用上下文");
    }

    public async Task<AppKnowledgeContextDto> SaveContextAsync(
        SaveAppKnowledgeContextRequest request,
        AppKnowledgeAccountScope expectedAccount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedAccount);
        using var operation = BeginOperation(expectedAccount);
        var response = await AwaitCurrentAsync(
            _api.SaveAppKnowledgeContextAsync(request, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        return RequireData(response?.Code, response?.Message, response?.Data, "保存应用上下文");
    }

    public async Task DeleteContextAsync(
        Guid id,
        AppKnowledgeAccountScope expectedAccount,
        CancellationToken ct = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("上下文标识无效。", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(expectedAccount);
        using var operation = BeginOperation(expectedAccount);
        var response = await AwaitCurrentAsync(
            _api.DeleteAppKnowledgeContextAsync(id, ct),
            operation,
            ct).ConfigureAwait(false);
        RequireCurrent(operation);
        RequireDeleteAcknowledgement(response?.Code, response?.Message, response?.Data, "已删除。", "删除应用上下文");
    }

    private Operation BeginOperation(AppKnowledgeAccountScope? expectedAccount = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var account = CurrentAccount
            ?? throw new InvalidOperationException("请先登录 PIM 账户；应用知识仅从当前账户读取。");
        if (expectedAccount is not null && account != NormalizeAccount(expectedAccount))
        {
            throw new InvalidOperationException("登录账号或服务器已切换；本次应用知识写操作已取消，未发送请求。");
        }
        if (!string.Equals(ToApiRoot(account.ServerUrl), ToApiRoot(_api.CurrentBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("服务器配置已变化，请刷新后重试。");
        }

        var operation = new Operation(account, Generation, _api.BeginOperationScope());
        try
        {
            RequireCurrent(operation);
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private async Task<T> AwaitCurrentAsync<T>(
        Task<T> request,
        Operation operation,
        CancellationToken ct)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RequireCurrent(operation);
            throw;
        }
    }

    private void RequireCurrent(Operation operation)
    {
        var current = CurrentAccount;
        if (current != operation.Account || Generation != operation.Generation)
        {
            throw new InvalidOperationException("登录账号或服务器已切换；已丢弃旧会话回执，请刷新后重试。");
        }

        try
        {
            _api.EnsureOperationScopeCurrent();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("登录凭据或服务器已改变；本次应用知识操作已中止。", ex);
        }
    }

    private static T RequireData<T>(int? code, string? message, T? data, string action)
        where T : class
    {
        if (code != 0)
        {
            throw new InvalidOperationException($"{action}失败（业务码 {code?.ToString() ?? "缺失"}）：{message ?? "服务器未提供错误说明"}");
        }

        return data ?? throw new InvalidDataException($"{action}失败：服务器返回空数据，未更新本地视图。");
    }

    private static void RequireDeleteAcknowledgement(
        int? code,
        string? message,
        string? data,
        string expectedAcknowledgement,
        string action)
    {
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"{action}失败（业务码 {code?.ToString() ?? "缺失"}）：{message ?? "服务器未提供错误说明"}");
        }

        if (!string.Equals(data, expectedAcknowledgement, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{action}失败：服务器未返回预期确认回执，未更新本地视图。");
        }
    }

    private static CategoryTreeNode? FindCategory(IEnumerable<CategoryTreeNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (Guid.TryParse(node.Id, out var nodeId) && nodeId == id)
            {
                return node;
            }

            var child = FindCategory(node.Children ?? [], id);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static Guid ParseGuid(string value, string name)
        => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidDataException($"{name} 不是有效 UUID。");

    private static AppKnowledgeAccountScope NormalizeAccount(AppKnowledgeAccountScope account)
    {
        var server = ApiClient.NormalizeServerUrl(account.ServerUrl);
        if (!Guid.TryParse(account.OwnerId, out var owner) || owner == Guid.Empty)
        {
            throw new InvalidDataException("当前登录账号标识无效。");
        }

        return new AppKnowledgeAccountScope(server, owner.ToString("D"));
    }

    private void OnExternalScopeChanged() => Interlocked.Increment(ref _generation);

    private static string ToApiRoot(string serverUrl)
    {
        var normalized = ApiClient.NormalizeServerUrl(serverUrl).TrimEnd('/');
        return normalized.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/api/v1";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _api.ExternalScopeChanged -= OnExternalScopeChanged;
        GC.SuppressFinalize(this);
    }

    private sealed class Operation(
        AppKnowledgeAccountScope account,
        long generation,
        ApiClient.ApiOperationScope apiScope) : IDisposable
    {
        public AppKnowledgeAccountScope Account { get; } = account;
        public long Generation { get; } = generation;
        public void Dispose() => apiScope.Dispose();
    }
}
