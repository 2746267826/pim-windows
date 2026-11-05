using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class AppKnowledgeServiceTests
{
    private static readonly Guid CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContextId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AppId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly AppKnowledgeAccountScope DefaultAccount =
        new("http://127.0.0.1:5858", AppId.ToString("D"));

    [Fact]
    public async Task DeleteCategoryAcceptsOnlyTheContractSuccessAcknowledgement()
    {
        var handler = new KnowledgeHandler();
        using var service = CreateService(handler);

        await service.DeleteCategoryAsync(CategoryId, DefaultAccount);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task DeleteContextAcceptsOnlyTheContractSuccessAcknowledgement()
    {
        var handler = new KnowledgeHandler();
        using var service = CreateService(handler);

        await service.DeleteContextAsync(ContextId, DefaultAccount);

        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task BuiltinCategoriesCannotBeEditedOrDeletedOverTheNetwork()
    {
        var editHandler = new KnowledgeHandler { CategoryIsBuiltin = true };
        using (var service = CreateService(editHandler))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveCategoryAsync(
                new CategorySaveRequest
                {
                    Id = CategoryId.ToString("D"),
                    Name = "Renamed",
                    Color = "#123456",
                    Icon = null,
                    Productivity = "neutral",
                    SortOrder = 10,
                    ParentId = null,
                },
                DefaultAccount));
        }

        Assert.Equal(1, editHandler.RequestCount);
        Assert.Equal("GET", editHandler.LastMethod);

        var deleteHandler = new KnowledgeHandler { CategoryIsBuiltin = true };
        using (var service = CreateService(deleteHandler))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DeleteCategoryAsync(CategoryId, DefaultAccount));
        }

        Assert.Equal(1, deleteHandler.RequestCount);
        Assert.Equal("GET", deleteHandler.LastMethod);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FALSE")]
    [InlineData("false")]
    [InlineData("deleted")]
    [InlineData("已删除。")]
    public async Task DeleteCategoryRejectsNonMatchingAcknowledgement(string? acknowledgement)
    {
        var handler = new KnowledgeHandler { CategoryDeleteAcknowledgement = acknowledgement };
        using var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DeleteCategoryAsync(CategoryId, DefaultAccount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FALSE")]
    [InlineData("false")]
    [InlineData("deleted")]
    [InlineData("已删除")]
    public async Task DeleteContextRejectsNonMatchingAcknowledgement(string? acknowledgement)
    {
        var handler = new KnowledgeHandler { ContextDeleteAcknowledgement = acknowledgement };
        using var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount));
    }

    [Fact]
    public async Task DeleteRejectsMissingDataAndNonzeroBusinessCode()
    {
        var missingData = new KnowledgeHandler { ContextDeleteAcknowledgement = null };
        using (var service = CreateService(missingData))
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DeleteContextAsync(ContextId, DefaultAccount));
        }

        var categoryErrorCode = new KnowledgeHandler { CategoryDeleteCode = 409 };
        using (var service = CreateService(categoryErrorCode))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DeleteCategoryAsync(CategoryId, DefaultAccount));
        }

        var contextErrorCode = new KnowledgeHandler { ContextDeleteCode = 409 };
        using (var service = CreateService(contextErrorCode))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DeleteContextAsync(ContextId, DefaultAccount));
        }
    }

    [Fact]
    public async Task ReadOperationsRejectNonzeroBusinessCodeAndNullData()
    {
        var categoryMissing = new KnowledgeHandler { CategoryTreeDataPresent = false };
        using (var service = CreateService(categoryMissing))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetCategoryTreeAsync());
        }

        var categoryError = new KnowledgeHandler { CategoryTreeCode = 503 };
        using (var service = CreateService(categoryError))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCategoryTreeAsync());
        }

        var appSearchError = new KnowledgeHandler { AppSearchCode = 503 };
        using (var service = CreateService(appSearchError))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAppsAsync("editor"));
        }

        var appsMissing = new KnowledgeHandler { AppSearchDataPresent = false };
        using (var service = CreateService(appsMissing))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.SearchAppsAsync("editor"));
        }

        var contextsError = new KnowledgeHandler { ContextCode = 503 };
        using (var service = CreateService(contextsError))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetContextsAsync(AppId));
        }

        var contextsMissing = new KnowledgeHandler { ContextDataPresent = false };
        using (var service = CreateService(contextsMissing))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetContextsAsync(AppId));
        }
    }

    [Fact]
    public async Task LateResponseFromInvalidatedAccountIsRejected()
    {
        var handler = new KnowledgeHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        var account = new AppKnowledgeAccountScope("http://127.0.0.1:5858", AppId.ToString("D"));
        using var service = new AppKnowledgeService(api, () => account);
        handler.AfterDelete = api.ClearAccessToken;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount));
        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task LateResponseAfterOwnerChangesOnSameServerIsRejected()
    {
        var handler = new KnowledgeHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        var ownerId = AppId.ToString("D");
        using var service = new AppKnowledgeService(
            api,
            () => new AppKnowledgeAccountScope("http://127.0.0.1:5858", ownerId));
        handler.AfterDelete = () => ownerId = Guid.Parse("44444444-4444-4444-4444-444444444444").ToString("D");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task LateResponseAfterServerChangesIsRejected()
    {
        var handler = new KnowledgeHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        var account = new AppKnowledgeAccountScope("http://127.0.0.1:5858", AppId.ToString("D"));
        using var service = new AppKnowledgeService(api, () => account);
        handler.AfterDelete = () => api.SetBaseUrl("http://127.0.0.1:5959");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount));
        Assert.Equal("DELETE", handler.LastMethod);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationIsPreservedWhenResponseBufferingIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new KnowledgeHandler { AfterDelete = cancellation.Cancel };
        using var service = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount, cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task LateResponseAfterCredentialChangesIsRejected()
    {
        var handler = new KnowledgeHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        var account = new AppKnowledgeAccountScope("http://127.0.0.1:5858", AppId.ToString("D"));
        using var service = new AppKnowledgeService(api, () => account);
        handler.AfterDelete = () => api.SetAccessToken("replacement-synthetic-token");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteContextAsync(ContextId, DefaultAccount));
        Assert.Equal("DELETE", handler.LastMethod);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExpectedScopeMismatchRejectsCreatesBeforeNetwork()
    {
        var handler = new KnowledgeHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        var current = DefaultAccount;
        using var service = new AppKnowledgeService(api, () => current);
        current = new AppKnowledgeAccountScope(
            "http://127.0.0.1:5858",
            "44444444-4444-4444-4444-444444444444");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveCategoryAsync(
            new CategorySaveRequest
            {
                Name = "Old account draft",
                Color = "#123456",
                Productivity = "neutral",
                SortOrder = 10,
            },
            DefaultAccount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveContextAsync(
            new SaveAppKnowledgeContextRequest
            {
                AppId = AppId.ToString("D"),
                ProcessName = "editor.exe",
                PatternType = "domain",
                PatternValue = "docs.example.test",
            },
            DefaultAccount));

        Assert.Equal(0, handler.RequestCount);
    }

    private static AppKnowledgeService CreateService(KnowledgeHandler handler)
    {
        var api = new ApiClient(() => handler);
        api.SetBaseUrl("http://127.0.0.1:5858");
        api.SetAccessToken("synthetic-token");
        return new AppKnowledgeService(
            api,
            () => DefaultAccount);
    }

    private sealed class KnowledgeHandler : HttpMessageHandler
    {
        public string? CategoryDeleteAcknowledgement { get; set; } = "已删除";
        public string? ContextDeleteAcknowledgement { get; set; } = "已删除。";
        public int CategoryDeleteCode { get; set; }
        public int ContextDeleteCode { get; set; }
        public int CategoryTreeCode { get; set; }
        public int AppSearchCode { get; set; }
        public int ContextCode { get; set; }
        public bool CategoryIsBuiltin { get; set; }
        public bool CategoryTreeDataPresent { get; set; } = true;
        public bool AppSearchDataPresent { get; set; } = true;
        public bool ContextDataPresent { get; set; } = true;
        public int RequestCount { get; private set; }
        public string? LastMethod { get; private set; }
        public Action? AfterDelete { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method.Method;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.EndsWith("/pc/categories/tree", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    code = CategoryTreeCode,
                    data = CategoryTreeDataPresent ? new[]
                    {
                        new
                        {
                            id = CategoryId, parentId = (Guid?)null, name = "Custom", color = "#123456",
                            productivity = "neutral", icon = (string?)null, sortOrder = 10,
                            isBuiltin = CategoryIsBuiltin, children = Array.Empty<object>(),
                        },
                    } : null,
                    message = "ok", timestamp = DateTimeOffset.UtcNow,
                }));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/pc/app-knowledge/apps", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    code = AppSearchCode, data = AppSearchDataPresent ? Array.Empty<object>() : null, message = "ok",
                    timestamp = DateTimeOffset.UtcNow,
                }));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/pc/app-knowledge/apps/" + AppId + "/contexts", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    code = ContextCode, data = ContextDataPresent ? Array.Empty<object>() : null, message = "ok",
                    timestamp = DateTimeOffset.UtcNow,
                }));
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/pc/categories/" + CategoryId, StringComparison.Ordinal))
            {
                AfterDelete?.Invoke();
                return Task.FromResult(Json(new
                {
                    code = CategoryDeleteCode, data = CategoryDeleteAcknowledgement, message = "ok", timestamp = DateTimeOffset.UtcNow,
                }));
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/pc/app-knowledge/contexts/" + ContextId, StringComparison.Ordinal))
            {
                AfterDelete?.Invoke();
                return Task.FromResult(Json(new
                {
                    code = ContextDeleteCode, data = ContextDeleteAcknowledgement, message = "ok",
                    timestamp = DateTimeOffset.UtcNow,
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
