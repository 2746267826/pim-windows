using System;
using System.Threading;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class LocalRuntimeAccountGuardTests
{
    [Fact]
    public void Run_IsPinnedToStartingAccount()
    {
        var guard = new LocalRuntimeAccountGuard();
        _ = guard.BeginRun("account-a");

        Assert.Throws<InvalidOperationException>(() => guard.BeginRun("account-b"));
    }

    [Fact]
    public async Task AccountSwitch_CancelsRunAndRevokesCapability()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        var result = await guard.SwitchAccountAsync("account-b");

        Assert.True(runToken.IsCancellationRequested);
        Assert.True(result.ActiveRunCancelled);
        Assert.True(result.CapabilityRevoked);
        Assert.Null(guard.Capability);
        Assert.Equal("account-b", guard.CurrentAccountId);
    }

    [Fact]
    public async Task SameOwnerOnDifferentServerOrigin_CancelsRunAndRevokesCapability()
    {
        var guard = new LocalRuntimeAccountGuard();
        var first = new LocalAccountScope("https://one.example", "account-a");
        var second = new LocalAccountScope("https://two.example", "account-a");
        var runToken = guard.BeginRun(first);
        guard.RegisterCapability(first, new[] { "model-a" });

        var result = await guard.SwitchScopeAsync(second);

        Assert.True(runToken.IsCancellationRequested);
        Assert.True(result.ActiveRunCancelled);
        Assert.True(result.CapabilityRevoked);
        Assert.Null(guard.Capability);
        Assert.Equal("account-a", guard.CurrentAccountId);
        Assert.Equal("https://two.example", guard.CurrentServerOrigin);
    }

    [Fact]
    public async Task SameOwnerOnSameHostDifferentBasePath_CancelsRunAndRevokesCapability()
    {
        var guard = new LocalRuntimeAccountGuard();
        var first = new LocalAccountScope("https://api.example.test/tenant-a", "account-a");
        var second = new LocalAccountScope("https://api.example.test/tenant-b", "account-a");
        var runToken = guard.BeginRun(first);
        guard.RegisterCapability(first, new[] { "model-a" });

        Assert.Equal(
            "https://api.example.test/tenant-a",
            Assert.IsType<NodeCapabilitySnapshot>(guard.Capability).ServerBaseAddress);

        var result = await guard.SwitchScopeAsync(second);

        Assert.True(runToken.IsCancellationRequested);
        Assert.True(result.ActiveRunCancelled);
        Assert.True(result.CapabilityRevoked);
        Assert.Null(guard.Capability);
        Assert.Equal("https://api.example.test", guard.CurrentServerOrigin);
        Assert.Equal("https://api.example.test/tenant-b", guard.CurrentServerBaseAddress);
    }

    [Fact]
    public void Run_IsPinnedToStartingServerOrigin()
    {
        var guard = new LocalRuntimeAccountGuard();
        _ = guard.BeginRun(new LocalAccountScope("https://one.example", "account-a"));

        Assert.Throws<InvalidOperationException>(() =>
            guard.BeginRun(new LocalAccountScope("https://two.example", "account-a")));
    }

    [Fact]
    public void OriginlessRun_CannotMatchScopedAccount()
    {
        var guard = new LocalRuntimeAccountGuard();
        _ = guard.BeginRun(new LocalAccountScope("https://one.example", "account-a"));

        Assert.Throws<InvalidOperationException>(() => guard.BeginRun("account-a"));
    }

    [Fact]
    public async Task SameScopeSwitch_DoesNotCancelRun()
    {
        var guard = new LocalRuntimeAccountGuard();
        var scope = new LocalAccountScope("https://one.example", "account-a");
        var runToken = guard.BeginRun(scope);
        guard.RegisterCapability(scope, new[] { "model-a" });

        var result = await guard.SwitchScopeAsync(scope);

        Assert.False(runToken.IsCancellationRequested);
        Assert.False(result.ActiveRunCancelled);
        Assert.False(result.CapabilityRevoked);
        Assert.NotNull(guard.Capability);
    }

    [Fact]
    public async Task SameAccountSwitch_DoesNotCancelRun()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        var result = await guard.SwitchAccountAsync("account-a");

        Assert.False(runToken.IsCancellationRequested);
        Assert.False(result.ActiveRunCancelled);
        Assert.False(result.CapabilityRevoked);
        Assert.NotNull(guard.Capability);
    }

    [Fact]
    public async Task Logout_RevokesCapabilityAndCancelsRun()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        await guard.SwitchAccountAsync(null);

        Assert.True(runToken.IsCancellationRequested);
        Assert.Null(guard.CurrentAccountId);
        Assert.Null(guard.Capability);
    }
}
